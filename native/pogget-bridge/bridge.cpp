#include "bridge.h"
#include "../pogget/PoggetCoreManager.hpp"
#include "../pogget/Flow/FlowRuntime.hpp"
#include <codecvt>
#include <cstring>
#include <locale>
#include <memory>
#include <unordered_map>
#include <unordered_set>
#ifdef _WIN32
#include <Windows.h>
#endif

namespace {
std::wstring wide(const char* text) {
    if (!text) return {};
#ifdef _WIN32
    const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, nullptr, 0);
    if (!length) return {};
    std::wstring value(static_cast<size_t>(length), L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, value.data(), length)) return {};
    value.pop_back();
    return value;
#else
    try { return std::wstring_convert<std::codecvt_utf8<wchar_t>>{}.from_bytes(text); }
    catch (...) { return {}; }
#endif
}
std::string utf8(const std::wstring& text) {
#ifdef _WIN32
    if (text.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (!size) return {};
    std::string value(size, '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), value.data(), size, nullptr, nullptr)) return {};
    return value;
#else
    try { return std::wstring_convert<std::codecvt_utf8<wchar_t>>{}.to_bytes(text); }
    catch (...) { return {}; }
#endif
}
bool validNodes(const std::vector<PoggetCore::Storage::FlowNode>& nodes) {
    for (const auto& node : nodes) {
        if (!PoggetCore::Flow::ValidateModule(node)) return false;
        for (const auto& branch : node.branches) if (!validNodes(branch.second)) return false;
    }
    return true;
}
bool parse(const char* json, PoggetCore::Storage::FlowDocument& document) {
    if (!json || !PoggetCore::Storage::ParseFlowJson(json, document)) return false;
    if (!document.trigger || !PoggetCore::Flow::ValidateModule(*document.trigger) ||
        !validNodes(document.actions) || !PoggetCore::Flow::ValidateFlowDataflow(document)) return false;
    return true;
}
}

struct dn_flow {
    dn_tip_callback callback;
    dn_file_executor executor;
    void* context;
    // Runtime invokes callbacks only from its worker, after releasing its mutex.
    PoggetCore::Flow::FlowRuntime runtime;
    dn_flow(dn_tip_callback cb, dn_file_executor file, void* ctx)
        : callback(cb), executor(file), context(ctx), runtime({}, makeAdapters()) {}
    PoggetCore::Flow::RuntimeAdapters makeAdapters() {
        PoggetCore::Flow::RuntimeAdapters adapters;
        adapters.showTip = [this](const std::wstring& title, const std::wstring& message) {
            if (!callback) return PoggetCore::Flow::ShowTipResult{false, false, L"no host prompt"};
            const auto t = utf8(title), m = utf8(message);
            const int response = callback(context, t.c_str(), m.c_str());
            if (response < 0) { runtime.Cancel(); return PoggetCore::Flow::ShowTipResult{false, false, L"cancelled"}; }
            return PoggetCore::Flow::ShowTipResult{true, response != 0, {}};
        };
        adapters.executeFileAction = [this](const std::filesystem::path& source,
            const std::filesystem::path& destination) {
            if (!executor) return PoggetCore::Flow::FileActionResult{false, false, {}, L"no host executor"};
            const auto from = utf8(source.wstring()), to = utf8(destination.wstring());
            char actual[32768]{};
            const int status = executor(context, from.c_str(), to.c_str(), actual, sizeof actual);
            if (status != 1) return PoggetCore::Flow::FileActionResult{false, true, {}, L"host refused action"};
            if (!actual[0] || !std::memchr(actual, '\0', sizeof actual))
                return PoggetCore::Flow::FileActionResult{false, true, {}, L"invalid host result"};
            const auto converted = wide(actual);
            if (converted.empty()) return PoggetCore::Flow::FileActionResult{false, true, {}, L"invalid result path"};
            return PoggetCore::Flow::FileActionResult{true, false, converted, {}};
        };
        return adapters;
    }
};

extern "C" {
int dn_layout(const dn_layout_config* config, dn_icon* icons, uint32_t count, dn_layout_result* result) {
    try {
        if (!config || !result || (count && !icons) || count > 100000 ||
            config->width <= 0 || config->width > 100000 ||
            config->height <= 0 || config->height > 100000 ||
            config->icon_size <= 0 || config->icon_size > 10000 ||
            config->gap < 0 || config->gap > 10000 ||
            config->columns <= 0 || config->columns > 10000 ||
            config->rows <= 0 || config->rows > 10000 || config->current_page < 0 ||
            config->flow_mode < -1 || config->flow_mode > 2) return 0;
        std::vector<PoggetCore::CoreIconLayoutData> input;
        input.reserve(count);
        std::unordered_set<uint64_t> ids;
        for (uint32_t i = 0; i < count; ++i) {
            if (!icons[i].name || !ids.insert(icons[i].id).second) return 0;
            auto name = wide(icons[i].name);
            if (name.empty() && icons[i].name[0]) return 0;
            PoggetCore::CoreIconLayoutData item;
            item.stableId = static_cast<int>(i); // upstream anchor index; caller ID stays 64-bit
            item.cachedName = std::move(name);
            item.path = item.cachedName;
            input.push_back(std::move(item));
        }
        PoggetCore::CoreContainerConfig native;
        native.EnablePagedLayout = config->flow_mode >= 0;
        native.PagedLayoutFlowMode = config->flow_mode;
        native.PagedLayoutMaxColumns = config->columns;
        native.PagedLayoutMaxRows = config->rows;
        native.PagedLayoutCurrentPage = config->current_page;
        native.IsListView = config->list_view != 0;
        std::vector<PoggetCore::CoreSectionHeaderLayoutData> headers;
        PoggetCore::CoreLayoutResult computed;
        PoggetCore::PoggetCoreManager::CalculatePositionsCore(input, headers, nullptr,
            config->width, config->height, 16, 16, config->icon_size, config->gap,
            false, false, [native](void*) { return native; },
            [](void*, const std::wstring&) { return false; }, nullptr, nullptr, &computed);
        for (const auto& item : input) {
            auto& output = icons[static_cast<size_t>(item.stableId)];
            output.x = item.targetX; output.y = item.targetY;
            output.page = item.pageIndex; output.visible = item.isVisible ? 1 : 0;
        }
        *result = { computed.pageCount, computed.currentPage, computed.columnsPerPage, computed.rowsPerPage };
        return 1;
    } catch (...) { return 0; }
}
int dn_flow_validate(const char* json) {
    try { PoggetCore::Storage::FlowDocument document; return parse(json, document) ? 1 : 0; }
    catch (...) { return 0; }
}
dn_flow* dn_flow_create(dn_tip_callback callback, void* context) {
    try { return new dn_flow(callback, nullptr, context); } catch (...) { return nullptr; }
}
dn_flow* dn_flow_create_with_executor(dn_tip_callback tip, dn_file_executor executor, void* context) {
    try { return new dn_flow(tip, executor, context); } catch (...) { return nullptr; }
}
int dn_flow_submit(dn_flow* flow, const char* json) {
    try {
        if (!flow) return 0;
        PoggetCore::Storage::FlowDocument document;
        return parse(json, document) && document.enabled && document.trigger &&
            document.trigger->type == PoggetCore::Flow::TriggerManual &&
            flow->runtime.RequestManualRun(std::move(document)) ? 1 : 0;
    } catch (...) { return 0; }
}
int dn_flow_busy(dn_flow* flow) {
    try { return flow ? (flow->runtime.Busy() ? 1 : 0) : -1; }
    catch (...) { return -1; }
}
void dn_flow_cancel(dn_flow* flow) { if (flow) flow->runtime.Cancel(); }
void dn_flow_destroy(dn_flow* flow) { delete flow; }
}
