#include "../../native/pogget-bridge/bridge.h"
#include "../../native/pogget/Flow/FlowRuntime.hpp"
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <string>
#include <thread>
#ifdef _WIN32
#include <Windows.h>
#ifdef _DEBUG
#include <crtdbg.h>
#endif
#endif
using namespace std::chrono_literals;
namespace F = PoggetCore::Flow;
namespace S = PoggetCore::Storage;

// Unlike assert, these checks execute in Release and retain test side effects.
#define CHECK(expr) do { if (!(expr)) { std::cerr << __FILE__ << ':' << __LINE__ << " failed: " #expr << '\n'; std::abort(); } } while (false)

static bool await(const std::function<bool()>& done) {
    for (int i = 0; i < 300; ++i) {
        if (done()) return true;
        std::this_thread::sleep_for(10ms);
    }
    return false;
}
static S::FlowDocument document(std::string triggerType, std::string actionType) {
    S::FlowDocument d;
    d.id = "flow"; d.name = "Unicode 测试";
    S::FlowNode trigger;
    trigger.id = "trigger"; trigger.type = std::move(triggerType);
    if (trigger.type == F::TriggerContainer) trigger.parameters = {
        {"containerId", "box"}, {"event", "added"}, {"intervalMs", std::int64_t(2600)}
    };
    d.trigger = trigger;
    S::FlowNode action;
    action.id = "tip"; action.type = std::move(actionType);
    action.parameters = {{"title", "标题 🐱"}, {"message", "中文 🐱 ${event.name}"}};
    d.actions.push_back(action);
    return d;
}
static std::atomic<int> callbackCount{0};
static std::atomic<int> rejected{0};
static int rejectMove(void*, const char* source, const char* requested, char*, uint32_t) {
    CHECK(std::string(source).find("🐱") != std::string::npos);
    CHECK(std::string(requested).find("🐱") != std::string::npos);
    ++rejected;
    return 0;
}
static int cancelTip(void* ctx, const char* title, const char* message) {
    auto* flow = static_cast<dn_flow**>(ctx);
    CHECK(std::string(title) == "标题 🐱");
    CHECK(std::string(message).find("中文 🐱") == 0);
    ++callbackCount;
    dn_flow_cancel(*flow); // Reentrant cancellation must not take a lock held by runtime.
    return -1;
}
int main() {
#ifdef _WIN32
    SetErrorMode(SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS);
#ifdef _DEBUG
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
#endif
    dn_layout_config config{180, 190, 48, 8, 2, 1, 0, 0, 0};
    dn_icon icons[] = {{10000000000ULL, "文件🐱"}, {2, "A"}, {3, "B"}, {4, "C"}, {5, "D"}};
    dn_layout_result result{};
    CHECK(dn_layout(&config, icons, 5, &result));
    CHECK(result.page_count >= 2 && icons[0].id == 10000000000ULL);
    CHECK(icons[0].page == 0 && icons[4].page > 0);
    CHECK(icons[0].x >= 0 && icons[0].y >= 0);
    dn_icon duplicate[] = {{1, "A"}, {1, "B"}};
    CHECK(!dn_layout(&config, duplicate, 2, &result));
    const auto doc = document(std::string(F::TriggerManual), std::string(F::InteractionTip));
    std::string json;
    S::FlowJson::Value decimal;
    S::FlowJson::Error decimalError;
    CHECK(S::FlowJson::Parse("1.25e-2", decimal, decimalError));
    CHECK(decimal.get_if<double>() && *decimal.get_if<double>() == 0.0125);
    CHECK(S::FlowJson::Stringify(decimal, json, decimalError));
    CHECK(S::FlowJson::Parse(json, decimal, decimalError));
    CHECK(decimal.get_if<double>() && *decimal.get_if<double>() == 0.0125);
    CHECK(!S::FlowJson::Parse("1.0e9999", decimal, decimalError));
    CHECK(S::SerializeFlowJson(doc, json));
    CHECK(dn_flow_validate(json.c_str()));
    CHECK(!dn_flow_validate("{\"actions\":[]}"));
    auto bad = doc;
    bad.actions[0].type = "unknown.action";
    CHECK(S::SerializeFlowJson(bad, json));
    CHECK(!dn_flow_validate(json.c_str()));
    bad = doc;
    bad.actions[0].inputs.push_back({"file", {"trigger", "file"}});
    CHECK(S::SerializeFlowJson(bad, json));
    CHECK(!dn_flow_validate(json.c_str())); // manual trigger has no file output
    CHECK(S::SerializeFlowJson(doc, json));
    dn_flow* flow = nullptr;
    flow = dn_flow_create(cancelTip, &flow);
    CHECK(flow && dn_flow_submit(flow, json.c_str()));
    CHECK(await([&] { return callbackCount.load() == 1; }));
    dn_flow_destroy(flow);

    const auto folder = std::filesystem::temp_directory_path() / "desknest-native-probe";
    std::filesystem::create_directories(folder);
    const auto source = folder / std::filesystem::path(std::u8string(u8"🐱.txt"));
    { std::ofstream file(source); file << "untouched"; }
    auto move = document(std::string(F::TriggerManual), std::string(F::ActionMoveFile));
    move.actions[0].parameters = {
        {"source", ""}, {"destinationDirectory", ""}
    };
    auto pathUtf8 = [](const std::filesystem::path& path) {
        const auto bytes = path.u8string();
        return std::string(bytes.begin(), bytes.end());
    };
    move.actions[0].parameters[0].second = pathUtf8(source);
    move.actions[0].parameters[1].second = pathUtf8(folder);
    auto afterRefusal = doc.actions[0];
    afterRefusal.id = "after-refusal";
    move.actions.push_back(afterRefusal);
    CHECK(S::SerializeFlowJson(move, json) && dn_flow_validate(json.c_str()));
    const int previousTips = callbackCount.load();
    flow = dn_flow_create_with_executor(cancelTip, rejectMove, &flow);
    CHECK(flow && dn_flow_submit(flow, json.c_str()));
    CHECK(await([&] { return rejected.load() == 1; }));
    dn_flow_destroy(flow);
    CHECK(callbackCount == previousTips); // a host refusal stops all later nodes
    std::cout << "executor callback done\n" << std::flush;
    std::error_code error;
    const bool stillThere = std::filesystem::exists(source, error);
    std::cout << "source exists=" << stillThere << " error=" << error.message() << "\n" << std::flush;
    CHECK(stillThere); // native never bypasses host refusal
    std::filesystem::remove(source, error);
    std::filesystem::remove(folder, error);
    std::cout << "executor refusal done\n" << std::flush;

    // Published Core snapshot, not a Vina catalog. 257 additions must leave baseline intact.
    std::atomic<int> prompts{0}, overflow{0};
    F::RuntimeAdapters adapters;
    adapters.showTip = [&](const std::wstring&, const std::wstring&) {
        ++prompts; return F::ShowTipResult{true, true, {}};
    };
    adapters.log = [&](const std::wstring&, const std::wstring& message) {
        if (message.find(L"baseline retained") != std::wstring::npos) ++overflow;
    };
    F::FlowRuntime runtime({}, adapters);
    auto automatic = document(std::string(F::TriggerContainer), std::string(F::InteractionTip));
    runtime.Publish({automatic});
    runtime.UpdateContainerSnapshots({{"box", {}}});
    runtime.RequestTick();
    CHECK(await([&] { return !runtime.Busy(); }));
    std::vector<std::filesystem::path> paths;
    for (int i = 0; i < 257; ++i) paths.emplace_back("file" + std::to_string(i));
    {
        // At exactly 256 changes the worker must advance, not livelock.
        std::atomic<int> exactPrompts{0}, exactOverflow{0};
        F::RuntimeAdapters exactAdapters;
        exactAdapters.showTip = [&](const std::wstring&, const std::wstring&) {
            ++exactPrompts; return F::ShowTipResult{true, true, {}};
        };
        exactAdapters.log = [&](const std::wstring&, const std::wstring& message) {
            if (message.find(L"baseline retained") != std::wstring::npos) ++exactOverflow;
        };
        F::FlowRuntime exact({}, exactAdapters);
        exact.Publish({automatic});
        exact.UpdateContainerSnapshots({{"box", {}}});
        exact.RequestTick();
        CHECK(await([&] { return !exact.Busy(); }));
        exact.UpdateContainerSnapshots({{"box", {paths.begin(), paths.begin() + 256}}});
        exact.RequestTick();
        CHECK(await([&] { return exactPrompts.load() == 256; }));
        exact.Stop();
        CHECK(exactOverflow == 0);
    }
    runtime.UpdateContainerSnapshots({{"box", paths}});
    runtime.RequestTick();
    std::cout << "overflow tick\n" << std::flush;
    CHECK(await([&] { return overflow.load() == 1; }));
    CHECK(prompts == 0);
    runtime.RequestTick();
    CHECK(await([&] { return overflow.load() == 2; })); // same 257 not silently advanced
    runtime.Cancel();
    runtime.Stop();
    std::cout << "layout, schema/dataflow, Unicode callback cancel, 257-event baseline passed\n";
}
