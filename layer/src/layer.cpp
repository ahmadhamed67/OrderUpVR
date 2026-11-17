// Order Up VR - Vulkan layer (milestone 1: interception plumbing)
//
// This layer sits between RPCS3 and the GPU driver. Milestone 1 only proves
// that we are inside RPCS3's Vulkan pipeline: it forwards every call, tracks
// the swapchain RPCS3 presents to, counts draws per frame and logs frame
// timing. Later milestones hang the OpenXR session off vkQueuePresentKHR
// (see docs/ARCHITECTURE.md).

#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <type_traits>
#include <unordered_map>
#include <vector>

#if defined(_WIN32)
#define OUVR_EXPORT extern "C" __declspec(dllexport)
#else
#define OUVR_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace ouvr
{
	// ---------------------------------------------------------------- logging
	static std::mutex g_log_mutex;
	static FILE* g_log = nullptr;

	static void log(const char* fmt, ...)
	{
		std::lock_guard lock(g_log_mutex);
		if (!g_log)
		{
			const char* path = std::getenv("ORDERUPVR_LOG");
			if (!path || !*path) path = "OrderUpVR_layer.log";
			g_log = std::fopen(path, "a");
			if (!g_log) return;
		}
		va_list args;
		va_start(args, fmt);
		std::vfprintf(g_log, fmt, args);
		va_end(args);
		std::fputc('\n', g_log);
		std::fflush(g_log);
	}

	// Dispatchable handles start with a pointer to the loader's dispatch table;
	// that pointer is the canonical key for per-instance/per-device data.
	template <typename T>
	static void* key(T handle) { return *reinterpret_cast<void**>(handle); }

	// ------------------------------------------------------------ instance data
	struct instance_data
	{
		VkInstance instance = VK_NULL_HANDLE;
		PFN_vkGetInstanceProcAddr gipa = nullptr;
		PFN_vkDestroyInstance DestroyInstance = nullptr;
		PFN_vkGetPhysicalDeviceProperties GetPhysicalDeviceProperties = nullptr;
	};

	// -------------------------------------------------------------- device data
	struct swapchain_data
	{
		VkExtent2D extent{};
		VkFormat format = VK_FORMAT_UNDEFINED;
		std::vector<VkImage> images;
	};

	struct device_data
	{
		VkDevice device = VK_NULL_HANDLE;
		VkPhysicalDevice physical = VK_NULL_HANDLE;
		PFN_vkGetDeviceProcAddr gdpa = nullptr;
		PFN_vkDestroyDevice DestroyDevice = nullptr;
		PFN_vkCreateSwapchainKHR CreateSwapchainKHR = nullptr;
		PFN_vkDestroySwapchainKHR DestroySwapchainKHR = nullptr;
		PFN_vkGetSwapchainImagesKHR GetSwapchainImagesKHR = nullptr;
		PFN_vkQueuePresentKHR QueuePresentKHR = nullptr;
		PFN_vkCmdDraw CmdDraw = nullptr;
		PFN_vkCmdDrawIndexed CmdDrawIndexed = nullptr;

		std::unordered_map<VkSwapchainKHR, swapchain_data> swapchains;
	};

	static std::mutex g_mutex;
	static std::unordered_map<void*, instance_data> g_instances;
	static std::unordered_map<void*, device_data> g_devices;

	static device_data* get_device(void* k)
	{
		std::lock_guard lock(g_mutex);
		auto it = g_devices.find(k);
		return it == g_devices.end() ? nullptr : &it->second;
	}

	static instance_data* get_instance(void* k)
	{
		std::lock_guard lock(g_mutex);
		auto it = g_instances.find(k);
		return it == g_instances.end() ? nullptr : &it->second;
	}

	// ----------------------------------------------------------- frame stats
	static std::atomic<uint64_t> g_draws_this_frame{0};
	static uint64_t g_frame = 0;
	static std::chrono::steady_clock::time_point g_window_start = std::chrono::steady_clock::now();
	static uint64_t g_window_frames = 0;
	static uint64_t g_window_draws = 0;

	// ---------------------------------------------------- layer chain helpers
	template <typename Info, VkStructureType SType>
	static auto* find_layer_link(const Info* create_info, VkLayerFunction func)
	{
		using link_t = std::conditional_t<SType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO,
			VkLayerInstanceCreateInfo, VkLayerDeviceCreateInfo>;
		auto* chain = reinterpret_cast<link_t*>(const_cast<void*>(create_info->pNext));
		while (chain && !(chain->sType == SType && chain->function == func))
			chain = reinterpret_cast<link_t*>(const_cast<void*>(chain->pNext));
		return chain;
	}

	// ======================================================== instance hooks
	static VKAPI_ATTR VkResult VKAPI_CALL CreateInstance(const VkInstanceCreateInfo* info,
		const VkAllocationCallbacks* alloc, VkInstance* out)
	{
		auto* link = find_layer_link<VkInstanceCreateInfo, VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO>(info, VK_LAYER_LINK_INFO);
		if (!link) return VK_ERROR_INITIALIZATION_FAILED;

		PFN_vkGetInstanceProcAddr next_gipa = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
		link->u.pLayerInfo = link->u.pLayerInfo->pNext; // advance for the next layer

		auto next_create = reinterpret_cast<PFN_vkCreateInstance>(next_gipa(VK_NULL_HANDLE, "vkCreateInstance"));
		const VkResult res = next_create(info, alloc, out);
		if (res != VK_SUCCESS) return res;

		instance_data data;
		data.instance = *out;
		data.gipa = next_gipa;
		data.DestroyInstance = reinterpret_cast<PFN_vkDestroyInstance>(next_gipa(*out, "vkDestroyInstance"));
		data.GetPhysicalDeviceProperties = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties>(next_gipa(*out, "vkGetPhysicalDeviceProperties"));

		{
			std::lock_guard lock(g_mutex);
			g_instances[key(*out)] = data;
		}

		const char* app = info->pApplicationInfo && info->pApplicationInfo->pApplicationName ? info->pApplicationInfo->pApplicationName : "?";
		log("[OrderUpVR] vkCreateInstance ok (app='%s', api=%u.%u)", app,
			info->pApplicationInfo ? VK_API_VERSION_MAJOR(info->pApplicationInfo->apiVersion) : 0,
			info->pApplicationInfo ? VK_API_VERSION_MINOR(info->pApplicationInfo->apiVersion) : 0);
		return VK_SUCCESS;
	}

	static VKAPI_ATTR void VKAPI_CALL DestroyInstance(VkInstance instance, const VkAllocationCallbacks* alloc)
	{
		PFN_vkDestroyInstance next = nullptr;
		{
			std::lock_guard lock(g_mutex);
			auto it = g_instances.find(key(instance));
			if (it != g_instances.end())
			{
				next = it->second.DestroyInstance;
				g_instances.erase(it);
			}
		}
		if (next) next(instance, alloc);
	}

	// ========================================================== device hooks
	static VKAPI_ATTR VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info,
		const VkAllocationCallbacks* alloc, VkDevice* out)
	{
		auto* link = find_layer_link<VkDeviceCreateInfo, VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO>(info, VK_LAYER_LINK_INFO);
		if (!link) return VK_ERROR_INITIALIZATION_FAILED;

		PFN_vkGetInstanceProcAddr next_gipa = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
		PFN_vkGetDeviceProcAddr next_gdpa = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
		link->u.pLayerInfo = link->u.pLayerInfo->pNext;

		auto next_create = reinterpret_cast<PFN_vkCreateDevice>(next_gipa(VK_NULL_HANDLE, "vkCreateDevice"));
		const VkResult res = next_create(physical, info, alloc, out);
		if (res != VK_SUCCESS) return res;

		device_data d;
		d.device = *out;
		d.physical = physical;
		d.gdpa = next_gdpa;
#define OUVR_LOAD(name) d.name = reinterpret_cast<PFN_vk##name>(next_gdpa(*out, "vk" #name))
		OUVR_LOAD(DestroyDevice);
		OUVR_LOAD(CreateSwapchainKHR);
		OUVR_LOAD(DestroySwapchainKHR);
		OUVR_LOAD(GetSwapchainImagesKHR);
		OUVR_LOAD(QueuePresentKHR);
		OUVR_LOAD(CmdDraw);
		OUVR_LOAD(CmdDrawIndexed);
#undef OUVR_LOAD

		{
			std::lock_guard lock(g_mutex);
			g_devices[key(*out)] = std::move(d);
		}

		// Name the GPU so logs show which adapter RPCS3 picked.
		if (auto* inst = get_instance(key(physical)); inst && inst->GetPhysicalDeviceProperties)
		{
			VkPhysicalDeviceProperties props{};
			inst->GetPhysicalDeviceProperties(physical, &props);
			log("[OrderUpVR] vkCreateDevice ok on '%s'", props.deviceName);
		}
		else
		{
			log("[OrderUpVR] vkCreateDevice ok");
		}
		return VK_SUCCESS;
	}

	static VKAPI_ATTR void VKAPI_CALL DestroyDevice(VkDevice device, const VkAllocationCallbacks* alloc)
	{
		PFN_vkDestroyDevice next = nullptr;
		{
			std::lock_guard lock(g_mutex);
			auto it = g_devices.find(key(device));
			if (it != g_devices.end())
			{
				next = it->second.DestroyDevice;
				g_devices.erase(it);
			}
		}
		if (next) next(device, alloc);
	}

	static VKAPI_ATTR VkResult VKAPI_CALL CreateSwapchainKHR(VkDevice device, const VkSwapchainCreateInfoKHR* info,
		const VkAllocationCallbacks* alloc, VkSwapchainKHR* out)
	{
		device_data* d = get_device(key(device));
		if (!d) return VK_ERROR_INITIALIZATION_FAILED;

		// Allow the swapchain to be read back later (OpenXR copy source).
		VkSwapchainCreateInfoKHR patched = *info;
		patched.imageUsage |= VK_IMAGE_USAGE_TRANSFER_SRC_BIT;

		VkResult res = d->CreateSwapchainKHR(device, &patched, alloc, out);
		if (res != VK_SUCCESS)
		{
			log("[OrderUpVR] swapchain with TRANSFER_SRC failed (%d), retrying unmodified", res);
			res = d->CreateSwapchainKHR(device, info, alloc, out);
			if (res != VK_SUCCESS) return res;
		}

		swapchain_data sc;
		sc.extent = info->imageExtent;
		sc.format = info->imageFormat;
		uint32_t count = 0;
		d->GetSwapchainImagesKHR(device, *out, &count, nullptr);
		sc.images.resize(count);
		d->GetSwapchainImagesKHR(device, *out, &count, sc.images.data());

		log("[OrderUpVR] swapchain created %ux%u format=%d images=%u", sc.extent.width, sc.extent.height,
			static_cast<int>(sc.format), count);

		std::lock_guard lock(g_mutex);
		d->swapchains[*out] = std::move(sc);
		return VK_SUCCESS;
	}

	static VKAPI_ATTR void VKAPI_CALL DestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain, const VkAllocationCallbacks* alloc)
	{
		device_data* d = get_device(key(device));
		if (!d) return;
		{
			std::lock_guard lock(g_mutex);
			d->swapchains.erase(swapchain);
		}
		d->DestroySwapchainKHR(device, swapchain, alloc);
	}

	static VKAPI_ATTR void VKAPI_CALL CmdDraw(VkCommandBuffer cmd, uint32_t vc, uint32_t ic, uint32_t fv, uint32_t fi)
	{
		g_draws_this_frame.fetch_add(1, std::memory_order_relaxed);
		if (device_data* d = get_device(key(cmd))) d->CmdDraw(cmd, vc, ic, fv, fi);
	}

	static VKAPI_ATTR void VKAPI_CALL CmdDrawIndexed(VkCommandBuffer cmd, uint32_t idx, uint32_t ic, uint32_t fi, int32_t vo, uint32_t finst)
	{
		g_draws_this_frame.fetch_add(1, std::memory_order_relaxed);
		if (device_data* d = get_device(key(cmd))) d->CmdDrawIndexed(cmd, idx, ic, fi, vo, finst);
	}

	static VKAPI_ATTR VkResult VKAPI_CALL QueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* info)
	{
		device_data* d = get_device(key(queue));
		if (!d) return VK_ERROR_DEVICE_LOST;

		// ---- Milestone 2 hook point: copy the presented image into an OpenXR
		// swapchain and submit it as a quad layer before forwarding. ----

		const uint64_t draws = g_draws_this_frame.exchange(0, std::memory_order_relaxed);
		++g_frame;
		++g_window_frames;
		g_window_draws += draws;

		const auto now = std::chrono::steady_clock::now();
		const double secs = std::chrono::duration<double>(now - g_window_start).count();
		if (secs >= 5.0)
		{
			log("[OrderUpVR] frame %llu: %.1f fps, %.0f draws/frame avg",
				static_cast<unsigned long long>(g_frame), g_window_frames / secs,
				g_window_frames ? static_cast<double>(g_window_draws) / g_window_frames : 0.0);
			g_window_start = now;
			g_window_frames = 0;
			g_window_draws = 0;
		}

		return d->QueuePresentKHR(queue, info);
	}

	// ======================================================= proc address
	static PFN_vkVoidFunction device_hook(const char* name)
	{
#define OUVR_HOOK(fn) if (std::strcmp(name, "vk" #fn) == 0) return reinterpret_cast<PFN_vkVoidFunction>(&fn)
		OUVR_HOOK(DestroyDevice);
		OUVR_HOOK(CreateSwapchainKHR);
		OUVR_HOOK(DestroySwapchainKHR);
		OUVR_HOOK(QueuePresentKHR);
		OUVR_HOOK(CmdDraw);
		OUVR_HOOK(CmdDrawIndexed);
#undef OUVR_HOOK
		return nullptr;
	}
}

OUVR_EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL OrderUpVR_GetDeviceProcAddr(VkDevice device, const char* name);

OUVR_EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL OrderUpVR_GetInstanceProcAddr(VkInstance instance, const char* name)
{
	using namespace ouvr;
	if (std::strcmp(name, "vkGetInstanceProcAddr") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&OrderUpVR_GetInstanceProcAddr);
	if (std::strcmp(name, "vkGetDeviceProcAddr") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&OrderUpVR_GetDeviceProcAddr);
	if (std::strcmp(name, "vkCreateInstance") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&CreateInstance);
	if (std::strcmp(name, "vkDestroyInstance") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&DestroyInstance);
	if (std::strcmp(name, "vkCreateDevice") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&CreateDevice);
	if (auto fn = device_hook(name)) return fn;

	if (instance == VK_NULL_HANDLE) return nullptr;
	instance_data* inst = get_instance(key(instance));
	return inst ? inst->gipa(instance, name) : nullptr;
}

OUVR_EXPORT VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL OrderUpVR_GetDeviceProcAddr(VkDevice device, const char* name)
{
	using namespace ouvr;
	if (std::strcmp(name, "vkGetDeviceProcAddr") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&OrderUpVR_GetDeviceProcAddr);
	if (auto fn = device_hook(name)) return fn;

	device_data* d = get_device(key(device));
	return d ? d->gdpa(device, name) : nullptr;
}
