#pragma once

#include "FlowJson.hpp"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <cwctype>
#include <optional>
#include <string>
#include <string_view>
#include <system_error>
#include <unordered_set>
#include <utility>
#include <vector>

#ifdef _WIN32
#include <Windows.h>
#endif

namespace PoggetCore::Storage {

    inline constexpr std::string_view FlowFormat = "pogget.flow";
    inline constexpr std::int64_t CurrentFlowSchemaVersion = 1;

    struct FlowInputBinding {
        std::string nodeId;
        std::string portId;

        bool operator==(const FlowInputBinding&) const = default;
    };

    struct FlowNode {
        using Branch = std::pair<std::string, std::vector<FlowNode>>;
        using Input = std::pair<std::string, FlowInputBinding>;
        using PortAlias = std::pair<std::string, std::string>;
        using VariableAlias = std::pair<std::string, std::string>;

        std::string id;
        std::string type;
        std::string displayName;
        std::int64_t version = 1;
        bool enabled = true;
        std::vector<Input> inputs;
        std::vector<PortAlias> portAliases;
        std::vector<VariableAlias> variableAliases;
        FlowJson::Value::Object parameters;
        std::vector<Branch> branches;
        FlowJson::Value::Object extensions;
    };

    struct FlowDocument {
        std::int64_t schemaVersion = CurrentFlowSchemaVersion;
        std::string id;
        std::int64_t revision = 0;
        std::string name;
        std::string publisher;
        std::string description;
        bool enabled = true;
        std::optional<FlowNode> trigger;
        std::vector<FlowNode> actions;
        FlowJson::Value::Object extensions;
    };

    struct FlowLimits {
        FlowJson::Limits json{};
        std::size_t maxNodes = 10'000;
        std::size_t maxFlowDepth = 32;
        std::size_t maxIdBytes = 256;
        std::size_t maxTypeBytes = 256;
        std::size_t maxPortIdBytes = 256;
        std::size_t maxInputsPerNode = 64;
        std::size_t maxPortAliasesPerNode = 64;
        std::size_t maxPortAliasBytes = 4 * 1024;
        std::size_t maxVariableAliasesPerNode = 64;
        std::size_t maxVariableAliasBytes = 4 * 1024;
        std::size_t maxNameBytes = 16 * 1024;
        std::size_t maxPublisherBytes = 4 * 1024;
        std::size_t maxDescriptionBytes = 64 * 1024;
        std::size_t maxBranchesPerNode = 32;
    };

    enum class FlowStorageError {
        none,
        recoveredBackup,
        invalidArgument,
        notLoaded,
        ioError,
        parseError,
        schemaError,
        resourceLimit,
        conflict,
        busy
    };

    struct FlowStorageResult {
        FlowStorageError error = FlowStorageError::none;
        std::string message;
        std::string path;
        std::size_t offset = 0;
        std::size_t line = 0;
        std::size_t column = 0;

        FlowStorageResult() = default;
        FlowStorageResult(FlowStorageError errorValue, std::string messageValue,
            std::string pathValue = {}, std::size_t offsetValue = 0,
            std::size_t lineValue = 0, std::size_t columnValue = 0)
            : error(errorValue), message(std::move(messageValue)), path(std::move(pathValue)),
            offset(offsetValue), line(lineValue), column(columnValue) {}

        bool ok() const noexcept {
            return error == FlowStorageError::none ||
                error == FlowStorageError::recoveredBackup;
        }
        explicit operator bool() const noexcept { return ok(); }
    };

    namespace FlowDetail {

        inline bool IsKnown(std::string_view key,
            std::initializer_list<std::string_view> known) noexcept {
            return std::find(known.begin(), known.end(), key) != known.end();
        }

        inline FlowStorageResult SchemaError(std::string path, std::string message) {
            return { FlowStorageError::schemaError, std::move(message), std::move(path) };
        }

        inline const FlowJson::Value* Required(const FlowJson::Value::Object& object,
            std::string_view key, const std::string& path, FlowStorageResult& result) {
            const auto* value = FlowJson::Find(object, key);
            if (!value) result = SchemaError(path + "." + std::string(key), "required member is missing");
            return value;
        }

        inline bool ReadRequiredString(const FlowJson::Value::Object& object,
            std::string_view key, const std::string& path, std::string& output,
            FlowStorageResult& result) {
            const auto* value = Required(object, key, path, result);
            if (!value) return false;
            const auto* text = value->get_if<std::string>();
            if (!text) {
                result = SchemaError(path + "." + std::string(key), "member must be a string");
                return false;
            }
            output = *text;
            return true;
        }

        inline bool ReadRequiredInteger(const FlowJson::Value::Object& object,
            std::string_view key, const std::string& path, std::int64_t& output,
            FlowStorageResult& result) {
            const auto* value = Required(object, key, path, result);
            if (!value) return false;
            const auto* integer = value->get_if<std::int64_t>();
            if (!integer) {
                result = SchemaError(path + "." + std::string(key), "member must be a signed 64-bit integer");
                return false;
            }
            output = *integer;
            return true;
        }

        inline bool ReadOptionalBool(const FlowJson::Value::Object& object,
            std::string_view key, const std::string& path, bool defaultValue, bool& output,
            FlowStorageResult& result) {
            const auto* value = FlowJson::Find(object, key);
            if (!value) {
                output = defaultValue;
                return true;
            }
            const auto* boolean = value->get_if<bool>();
            if (!boolean) {
                result = SchemaError(path + "." + std::string(key), "member must be a boolean");
                return false;
            }
            output = *boolean;
            return true;
        }

        inline bool ReadOptionalString(const FlowJson::Value::Object& object,
            std::string_view key, const std::string& path, std::string& output,
            FlowStorageResult& result) {
            const auto* value = FlowJson::Find(object, key);
            if (!value) {
                output.clear();
                return true;
            }
            const auto* text = value->get_if<std::string>();
            if (!text) {
                result = SchemaError(path + "." + std::string(key), "member must be a string");
                return false;
            }
            output = *text;
            return true;
        }

        inline void CopyExtensions(const FlowJson::Value::Object& source,
            std::initializer_list<std::string_view> known, FlowJson::Value::Object& output) {
            output.clear();
            for (const auto& member : source) {
                if (!IsKnown(member.first, known)) output.push_back(member);
            }
        }

        inline bool ParseNode(const FlowJson::Value& value, const std::string& path,
            FlowNode& output, FlowStorageResult& result, const FlowLimits& limits,
            std::unordered_set<std::string>& ids, std::size_t depth, std::size_t& nodeCount) {
            if (limits.maxFlowDepth != 0 && depth > limits.maxFlowDepth) {
                result = { FlowStorageError::resourceLimit, "flow nesting exceeds the depth limit", path };
                return false;
            }
            if (limits.maxNodes != 0 && ++nodeCount > limits.maxNodes) {
                result = { FlowStorageError::resourceLimit, "flow node count exceeds the limit", path };
                return false;
            }
            const auto* object = value.get_if<FlowJson::Value::Object>();
            if (!object) {
                result = SchemaError(path, "flow node must be an object");
                return false;
            }
            if (!ReadRequiredString(*object, "id", path, output.id, result) ||
                !ReadRequiredString(*object, "type", path, output.type, result) ||
                !ReadOptionalString(*object, "displayName", path, output.displayName, result) ||
                !ReadRequiredInteger(*object, "version", path, output.version, result) ||
                !ReadOptionalBool(*object, "enabled", path, true, output.enabled, result)) return false;

            if (output.id.empty()) {
                result = SchemaError(path + ".id", "node id cannot be empty");
                return false;
            }
            if (limits.maxIdBytes != 0 && output.id.size() > limits.maxIdBytes) {
                result = { FlowStorageError::resourceLimit, "node id exceeds the byte limit", path + ".id" };
                return false;
            }
            if (!ids.emplace(output.id).second) {
                result = SchemaError(path + ".id", "node id must be unique within the document");
                return false;
            }
            if (output.type.empty()) {
                result = SchemaError(path + ".type", "node type cannot be empty");
                return false;
            }
            if (limits.maxTypeBytes != 0 && output.type.size() > limits.maxTypeBytes) {
                result = { FlowStorageError::resourceLimit, "node type exceeds the byte limit", path + ".type" };
                return false;
            }
            if (output.version <= 0) {
                result = SchemaError(path + ".version", "node version must be positive");
                return false;
            }
            if (limits.maxNameBytes != 0 && output.displayName.size() > limits.maxNameBytes) {
                result = { FlowStorageError::resourceLimit,
                    "node display name exceeds the byte limit", path + ".displayName" };
                return false;
            }

            output.inputs.clear();
            if (const auto* inputs = FlowJson::Find(*object, "inputs")) {
                const auto* inputObject = inputs->get_if<FlowJson::Value::Object>();
                if (!inputObject) {
                    result = SchemaError(path + ".inputs", "node inputs must be an object");
                    return false;
                }
                if (limits.maxInputsPerNode != 0 &&
                    inputObject->size() > limits.maxInputsPerNode) {
                    result = { FlowStorageError::resourceLimit,
                        "node input count exceeds the limit", path + ".inputs" };
                    return false;
                }
                output.inputs.reserve(inputObject->size());
                for (const auto& inputMember : *inputObject) {
                    const auto inputPath = path + ".inputs." + inputMember.first;
                    if (inputMember.first.empty()) {
                        result = SchemaError(path + ".inputs", "input port id cannot be empty");
                        return false;
                    }
                    if (limits.maxPortIdBytes != 0 &&
                        inputMember.first.size() > limits.maxPortIdBytes) {
                        result = { FlowStorageError::resourceLimit,
                            "input port id exceeds the byte limit", inputPath };
                        return false;
                    }
                    const auto* bindingObject =
                        inputMember.second.get_if<FlowJson::Value::Object>();
                    if (!bindingObject) {
                        result = SchemaError(inputPath, "input binding must be an object");
                        return false;
                    }
                    FlowInputBinding binding;
                    if (!ReadRequiredString(*bindingObject, "nodeId", inputPath,
                        binding.nodeId, result) ||
                        !ReadRequiredString(*bindingObject, "portId", inputPath,
                            binding.portId, result)) return false;
                    if (binding.nodeId.empty() || binding.portId.empty()) {
                        result = SchemaError(inputPath,
                            "input binding nodeId and portId cannot be empty");
                        return false;
                    }
                    if ((limits.maxIdBytes != 0 && binding.nodeId.size() > limits.maxIdBytes) ||
                        (limits.maxPortIdBytes != 0 &&
                            binding.portId.size() > limits.maxPortIdBytes)) {
                        result = { FlowStorageError::resourceLimit,
                            "input binding identifier exceeds the byte limit", inputPath };
                        return false;
                    }
                    output.inputs.emplace_back(inputMember.first, std::move(binding));
                }
            }

            output.portAliases.clear();
            if (const auto* aliases = FlowJson::Find(*object, "portAliases")) {
                const auto* aliasObject = aliases->get_if<FlowJson::Value::Object>();
                if (!aliasObject) {
                    result = SchemaError(path + ".portAliases",
                        "node port aliases must be an object");
                    return false;
                }
                if (limits.maxPortAliasesPerNode != 0 &&
                    aliasObject->size() > limits.maxPortAliasesPerNode) {
                    result = { FlowStorageError::resourceLimit,
                        "node port alias count exceeds the limit", path + ".portAliases" };
                    return false;
                }
                output.portAliases.reserve(aliasObject->size());
                for (const auto& aliasMember : *aliasObject) {
                    const auto aliasPath = path + ".portAliases." + aliasMember.first;
                    const auto* alias = aliasMember.second.get_if<std::string>();
                    if (aliasMember.first.empty() || !alias) {
                        result = SchemaError(aliasPath,
                            "port alias must map a non-empty port id to a string");
                        return false;
                    }
                    if ((limits.maxPortIdBytes != 0 &&
                        aliasMember.first.size() > limits.maxPortIdBytes) ||
                        (limits.maxPortAliasBytes != 0 &&
                            alias->size() > limits.maxPortAliasBytes)) {
                        result = { FlowStorageError::resourceLimit,
                            "port alias exceeds the byte limit", aliasPath };
                        return false;
                    }
                    output.portAliases.emplace_back(aliasMember.first, *alias);
                }
            }

            output.variableAliases.clear();
            if (const auto* aliases = FlowJson::Find(*object, "variableAliases")) {
                const auto* aliasObject = aliases->get_if<FlowJson::Value::Object>();
                if (!aliasObject) {
                    result = SchemaError(path + ".variableAliases",
                        "node variable aliases must be an object");
                    return false;
                }
                if (limits.maxVariableAliasesPerNode != 0 &&
                    aliasObject->size() > limits.maxVariableAliasesPerNode) {
                    result = { FlowStorageError::resourceLimit,
                        "node variable alias count exceeds the limit",
                        path + ".variableAliases" };
                    return false;
                }
                output.variableAliases.reserve(aliasObject->size());
                for (const auto& aliasMember : *aliasObject) {
                    const auto aliasPath = path + ".variableAliases." + aliasMember.first;
                    const auto* alias = aliasMember.second.get_if<std::string>();
                    if (aliasMember.first.empty() || !alias || alias->empty()) {
                        result = SchemaError(aliasPath,
                            "variable alias must map a non-empty port id to a non-empty string");
                        return false;
                    }
                    if ((limits.maxPortIdBytes != 0 &&
                        aliasMember.first.size() > limits.maxPortIdBytes) ||
                        (limits.maxVariableAliasBytes != 0 &&
                            alias->size() > limits.maxVariableAliasBytes)) {
                        result = { FlowStorageError::resourceLimit,
                            "variable alias exceeds the byte limit", aliasPath };
                        return false;
                    }
                    output.variableAliases.emplace_back(aliasMember.first, *alias);
                }
            }

            output.parameters.clear();
            if (const auto* parameters = FlowJson::Find(*object, "parameters")) {
                const auto* parameterObject = parameters->get_if<FlowJson::Value::Object>();
                if (!parameterObject) {
                    result = SchemaError(path + ".parameters", "node parameters must be an object");
                    return false;
                }
                output.parameters = *parameterObject;
            }

            output.branches.clear();
            if (const auto* branches = FlowJson::Find(*object, "branches")) {
                const auto* branchObject = branches->get_if<FlowJson::Value::Object>();
                if (!branchObject) {
                    result = SchemaError(path + ".branches", "node branches must be an object");
                    return false;
                }
                if (limits.maxBranchesPerNode != 0 &&
                    branchObject->size() > limits.maxBranchesPerNode) {
                    result = { FlowStorageError::resourceLimit,
                        "node branch count exceeds the limit", path + ".branches" };
                    return false;
                }
                for (const auto& branchMember : *branchObject) {
                    if (branchMember.first.empty()) {
                        result = SchemaError(path + ".branches", "branch name cannot be empty");
                        return false;
                    }
                    const auto* array = branchMember.second.get_if<FlowJson::Value::Array>();
                    if (!array) {
                        result = SchemaError(path + ".branches." + branchMember.first,
                            "branch must be an array of flow nodes");
                        return false;
                    }
                    std::vector<FlowNode> nodes;
                    nodes.reserve(array->size());
                    for (std::size_t index = 0; index < array->size(); ++index) {
                        FlowNode node;
                        const auto nodePath = path + ".branches." + branchMember.first +
                            "[" + std::to_string(index) + "]";
                        if (!ParseNode((*array)[index], nodePath, node, result, limits,
                            ids, depth + 1, nodeCount)) return false;
                        nodes.push_back(std::move(node));
                    }
                    output.branches.emplace_back(branchMember.first, std::move(nodes));
                }
            }

            CopyExtensions(*object, { "id", "type", "displayName", "version", "enabled",
                "inputs", "portAliases", "variableAliases", "parameters", "branches" },
                output.extensions);
            return true;
        }

        inline FlowJson::Value EncodeNode(const FlowNode& node) {
            FlowJson::Value::Object object{
                { "id", node.id },
                { "type", node.type },
                { "version", node.version },
                { "enabled", node.enabled },
                { "parameters", node.parameters }
            };
            if (!node.displayName.empty())
                object.emplace_back("displayName", node.displayName);
            if (!node.inputs.empty()) {
                FlowJson::Value::Object inputs;
                inputs.reserve(node.inputs.size());
                for (const auto& input : node.inputs) {
                    inputs.emplace_back(input.first, FlowJson::Value::Object{
                        { "nodeId", input.second.nodeId },
                        { "portId", input.second.portId }
                    });
                }
                object.emplace_back("inputs", std::move(inputs));
            }
            if (!node.portAliases.empty()) {
                FlowJson::Value::Object aliases;
                aliases.reserve(node.portAliases.size());
                for (const auto& alias : node.portAliases)
                    aliases.emplace_back(alias.first, alias.second);
                object.emplace_back("portAliases", std::move(aliases));
            }
            if (!node.variableAliases.empty()) {
                FlowJson::Value::Object aliases;
                aliases.reserve(node.variableAliases.size());
                for (const auto& alias : node.variableAliases)
                    aliases.emplace_back(alias.first, alias.second);
                object.emplace_back("variableAliases", std::move(aliases));
            }
            if (!node.branches.empty()) {
                FlowJson::Value::Object branches;
                branches.reserve(node.branches.size());
                for (const auto& branch : node.branches) {
                    FlowJson::Value::Array nodes;
                    nodes.reserve(branch.second.size());
                    for (const auto& child : branch.second) nodes.push_back(EncodeNode(child));
                    branches.emplace_back(branch.first, std::move(nodes));
                }
                object.emplace_back("branches", std::move(branches));
            }
            for (const auto& extension : node.extensions) {
                if (!IsKnown(extension.first,
                    { "id", "type", "displayName", "version", "enabled", "inputs",
                        "portAliases", "variableAliases", "parameters", "branches" }))
                    object.push_back(extension);
            }
            return FlowJson::Value(std::move(object));
        }

        inline bool HasJsonExtension(const std::filesystem::path& path) {
            auto extension = path.extension().string();
            std::transform(extension.begin(), extension.end(), extension.begin(),
                [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
            return extension == ".json";
        }

        inline bool SamePath(const std::filesystem::path& left,
            const std::filesystem::path& right) {
            std::error_code leftError;
            std::error_code rightError;
            auto normalizedLeft = std::filesystem::absolute(left, leftError).lexically_normal();
            auto normalizedRight = std::filesystem::absolute(right, rightError).lexically_normal();
            if (leftError) normalizedLeft = left.lexically_normal();
            if (rightError) normalizedRight = right.lexically_normal();
#ifdef _WIN32
            auto leftText = normalizedLeft.wstring();
            auto rightText = normalizedRight.wstring();
            std::transform(leftText.begin(), leftText.end(), leftText.begin(),
                [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            std::transform(rightText.begin(), rightText.end(), rightText.begin(),
                [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            return leftText == rightText;
#else
            return normalizedLeft == normalizedRight;
#endif
        }

        struct FileStamp {
            bool exists = false;
            std::uintmax_t size = 0;
            std::filesystem::file_time_type writeTime{};
            std::uint64_t fingerprint = 0;

            bool operator==(const FileStamp& other) const noexcept {
                return exists == other.exists && (!exists ||
                    (size == other.size && writeTime == other.writeTime &&
                        fingerprint == other.fingerprint));
            }
        };

        inline FileStamp GetFileStamp(const std::filesystem::path& path) {
            std::error_code error;
            FileStamp stamp;
            stamp.exists = std::filesystem::is_regular_file(path, error);
            if (error || !stamp.exists) return stamp;
            stamp.size = std::filesystem::file_size(path, error);
            if (error) return {};
            stamp.writeTime = std::filesystem::last_write_time(path, error);
            if (error) return {};
            std::ifstream input(path, std::ios::binary);
            if (!input.is_open()) return {};
            std::uint64_t hash = 1469598103934665603ULL;
            char buffer[64 * 1024];
            while (input) {
                input.read(buffer, sizeof(buffer));
                const auto count = input.gcount();
                for (std::streamsize i = 0; i < count; ++i) {
                    hash ^= static_cast<unsigned char>(buffer[i]);
                    hash *= 1099511628211ULL;
                }
            }
            if (!input.eof()) return {};
            stamp.fingerprint = hash;
            return stamp;
        }

        inline FlowStorageResult ReadFile(const std::filesystem::path& path,
            std::string& bytes, const FlowLimits& limits) {
            std::ifstream input(path, std::ios::binary | std::ios::ate);
            if (!input.is_open()) return { FlowStorageError::ioError, "could not open flow JSON file" };
            const auto size = input.tellg();
            if (size < 0) return { FlowStorageError::ioError, "could not determine flow JSON file size" };
            if (limits.json.maxInputBytes != 0 &&
                static_cast<std::uintmax_t>(size) > limits.json.maxInputBytes)
                return { FlowStorageError::resourceLimit, "flow JSON file exceeds the byte limit" };
            bytes.resize(static_cast<std::size_t>(size));
            input.seekg(0);
            if (!bytes.empty() && !input.read(bytes.data(), static_cast<std::streamsize>(bytes.size())))
                return { FlowStorageError::ioError, "could not read complete flow JSON file" };
            if (bytes.size() >= 3 && static_cast<unsigned char>(bytes[0]) == 0xEF &&
                static_cast<unsigned char>(bytes[1]) == 0xBB &&
                static_cast<unsigned char>(bytes[2]) == 0xBF) bytes.erase(0, 3);
            return {};
        }

#ifdef _WIN32
        class ProcessLock {
        public:
            explicit ProcessLock(const std::filesystem::path& path) {
                std::error_code error;
                auto normalized = std::filesystem::absolute(path, error).lexically_normal().wstring();
                if (error) normalized = path.wstring();
                std::uint64_t hash = 1469598103934665603ULL;
                for (wchar_t c : normalized) {
                    c = static_cast<wchar_t>(std::towlower(c));
                    hash ^= static_cast<std::uint64_t>(c);
                    hash *= 1099511628211ULL;
                }
                const auto name = L"Local\\PoggetFlowStorage_" + std::to_wstring(hash);
                handle_ = CreateMutexW(nullptr, FALSE, name.c_str());
                if (handle_) {
                    const DWORD wait = WaitForSingleObject(handle_, 5000);
                    locked_ = wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED;
                }
            }
            ~ProcessLock() {
                if (locked_) ReleaseMutex(handle_);
                if (handle_) CloseHandle(handle_);
            }
            bool locked() const noexcept { return locked_; }
        private:
            HANDLE handle_ = nullptr;
            bool locked_ = false;
        };
#endif

        inline FlowStorageResult WriteAtomic(const std::filesystem::path& path,
            const std::filesystem::path& backupPath, std::string_view bytes,
            const std::optional<FileStamp>& expectedStamp = std::nullopt) {
            std::error_code error;
            if (!path.parent_path().empty()) std::filesystem::create_directories(path.parent_path(), error);
            if (error) return { FlowStorageError::ioError, "could not create flow JSON directory" };
            error.clear();
            if (!backupPath.parent_path().empty())
                std::filesystem::create_directories(backupPath.parent_path(), error);
            if (error) return { FlowStorageError::ioError, "could not create flow backup directory" };

            static std::atomic<std::uint64_t> sequence{ 0 };
#ifdef _WIN32
            const auto tempPath = std::filesystem::path(path.wstring() + L".tmp." +
                std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(++sequence));
#else
            const auto tempPath = std::filesystem::path(path.string() + ".tmp." +
                std::to_string(++sequence));
#endif
            {
                std::ofstream output(tempPath, std::ios::binary | std::ios::trunc);
                if (!output.is_open()) return { FlowStorageError::ioError, "could not open temporary flow file" };
                output.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
                output.flush();
                if (!output.good()) {
                    output.close();
                    std::filesystem::remove(tempPath, error);
                    return { FlowStorageError::ioError, "could not write complete temporary flow file" };
                }
            }

#ifdef _WIN32
            HANDLE file = CreateFileW(tempPath.c_str(), GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                FILE_FLAG_WRITE_THROUGH, nullptr);
            if (file == INVALID_HANDLE_VALUE) {
                std::filesystem::remove(tempPath, error);
                return { FlowStorageError::ioError, "could not reopen temporary flow file for flushing" };
            }
            const bool flushed = FlushFileBuffers(file) != FALSE;
            CloseHandle(file);
            if (!flushed) {
                std::filesystem::remove(tempPath, error);
                return { FlowStorageError::ioError, "could not flush temporary flow file" };
            }
            if (expectedStamp && !(GetFileStamp(path) == *expectedStamp)) {
                std::filesystem::remove(tempPath, error);
                return { FlowStorageError::conflict,
                    "flow JSON file changed while the replacement was being prepared" };
            }
            const bool originalExists = std::filesystem::is_regular_file(path, error);
            bool replaced = false;
            for (int attempt = 0; attempt < 5; ++attempt) {
                if (originalExists) {
                    replaced = ReplaceFileW(path.c_str(), tempPath.c_str(), backupPath.c_str(),
                        REPLACEFILE_WRITE_THROUGH, nullptr, nullptr) != FALSE;
                }
                else {
                    replaced = MoveFileExW(tempPath.c_str(), path.c_str(),
                        MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
                }
                if (replaced) break;
                const DWORD publishError = GetLastError();
                if (publishError != ERROR_SHARING_VIOLATION && publishError != ERROR_ACCESS_DENIED)
                    break;
                Sleep(static_cast<DWORD>(20 * (attempt + 1)));
            }
            if (!replaced) {
                std::filesystem::remove(tempPath, error);
                return { FlowStorageError::ioError, "could not atomically publish flow JSON file" };
            }
#else
            if (expectedStamp && !(GetFileStamp(path) == *expectedStamp)) {
                std::filesystem::remove(tempPath, error);
                return { FlowStorageError::conflict,
                    "flow JSON file changed while the replacement was being prepared" };
            }
            if (std::filesystem::is_regular_file(path, error)) {
                error.clear();
                std::filesystem::copy_file(path, backupPath,
                    std::filesystem::copy_options::overwrite_existing, error);
                if (error) {
                    std::filesystem::remove(tempPath, error);
                    return { FlowStorageError::ioError, "could not create flow JSON backup" };
                }
            }
            error.clear();
            std::filesystem::rename(tempPath, path, error);
            if (error) {
                std::filesystem::remove(tempPath, error);
                return { FlowStorageError::ioError, "could not publish flow JSON file" };
            }
#endif
            return {};
        }
    }

    inline FlowStorageResult ParseFlowJson(std::string_view json, FlowDocument& output,
        const FlowLimits& limits = {}) {
        FlowJson::Value root;
        FlowJson::Error jsonError;
        if (!FlowJson::Parse(json, root, jsonError, limits.json)) {
            const bool resource = jsonError.message.find("limit") != std::string::npos;
            return { resource ? FlowStorageError::resourceLimit : FlowStorageError::parseError,
                jsonError.message, "$", jsonError.offset, jsonError.line, jsonError.column };
        }
        const auto* object = root.get_if<FlowJson::Value::Object>();
        if (!object) return FlowDetail::SchemaError("$", "flow document must be a JSON object");

        FlowDocument parsed;
        FlowStorageResult result;
        std::string format;
        if (!FlowDetail::ReadRequiredString(*object, "format", "$", format, result) ||
            !FlowDetail::ReadRequiredInteger(*object, "schemaVersion", "$", parsed.schemaVersion, result) ||
            !FlowDetail::ReadRequiredString(*object, "id", "$", parsed.id, result) ||
            !FlowDetail::ReadRequiredString(*object, "name", "$", parsed.name, result) ||
            !FlowDetail::ReadOptionalString(*object, "publisher", "$", parsed.publisher, result) ||
            !FlowDetail::ReadOptionalString(*object, "description", "$", parsed.description, result) ||
            !FlowDetail::ReadOptionalBool(*object, "enabled", "$", true, parsed.enabled, result)) return result;
        if (format != FlowFormat)
            return FlowDetail::SchemaError("$.format", "JSON document is not a Pogget flow");
        if (parsed.schemaVersion != CurrentFlowSchemaVersion)
            return FlowDetail::SchemaError("$.schemaVersion", "unsupported flow schema version");
        if (parsed.id.empty()) return FlowDetail::SchemaError("$.id", "flow id cannot be empty");
        if (limits.maxIdBytes != 0 && parsed.id.size() > limits.maxIdBytes)
            return { FlowStorageError::resourceLimit, "flow id exceeds the byte limit", "$.id" };
        if (limits.maxNameBytes != 0 && parsed.name.size() > limits.maxNameBytes)
            return { FlowStorageError::resourceLimit, "flow name exceeds the byte limit", "$.name" };
        if (limits.maxPublisherBytes != 0 && parsed.publisher.size() > limits.maxPublisherBytes)
            return { FlowStorageError::resourceLimit, "flow publisher exceeds the byte limit", "$.publisher" };
        if (limits.maxDescriptionBytes != 0 && parsed.description.size() > limits.maxDescriptionBytes)
            return { FlowStorageError::resourceLimit, "flow description exceeds the byte limit", "$.description" };

        if (const auto* revision = FlowJson::Find(*object, "revision")) {
            const auto* integer = revision->get_if<std::int64_t>();
            if (!integer) return FlowDetail::SchemaError("$.revision", "revision must be an integer");
            parsed.revision = *integer;
        }
        if (parsed.revision < 0)
            return FlowDetail::SchemaError("$.revision", "revision cannot be negative");

        std::unordered_set<std::string> nodeIds;
        std::size_t nodeCount = 0;
        if (const auto* trigger = FlowJson::Find(*object, "trigger")) {
            if (!std::holds_alternative<std::nullptr_t>(trigger->data)) {
                FlowNode node;
                if (!FlowDetail::ParseNode(*trigger, "$.trigger", node, result, limits,
                    nodeIds, 1, nodeCount)) return result;
                parsed.trigger = std::move(node);
            }
        }

        const auto* actionsValue = FlowDetail::Required(*object, "actions", "$", result);
        if (!actionsValue) return result;
        const auto* actions = actionsValue->get_if<FlowJson::Value::Array>();
        if (!actions) return FlowDetail::SchemaError("$.actions", "actions must be an array");
        parsed.actions.reserve(actions->size());
        for (std::size_t index = 0; index < actions->size(); ++index) {
            FlowNode node;
            const auto path = "$.actions[" + std::to_string(index) + "]";
            if (!FlowDetail::ParseNode((*actions)[index], path, node, result, limits,
                nodeIds, 1, nodeCount)) return result;
            parsed.actions.push_back(std::move(node));
        }
        FlowDetail::CopyExtensions(*object,
            { "format", "schemaVersion", "id", "revision", "name", "publisher", "description", "enabled", "trigger", "actions" },
            parsed.extensions);
        output = std::move(parsed);
        return {};
    }

    inline FlowStorageResult SerializeFlowJson(const FlowDocument& document,
        std::string& json, const FlowLimits& limits = {}) {
        FlowJson::Value::Array actions;
        actions.reserve(document.actions.size());
        for (const auto& action : document.actions) actions.push_back(FlowDetail::EncodeNode(action));
        FlowJson::Value::Object root{
            { "format", std::string(FlowFormat) },
            { "schemaVersion", document.schemaVersion },
            { "id", document.id },
            { "revision", document.revision },
            { "name", document.name },
            { "publisher", document.publisher },
            { "description", document.description },
            { "enabled", document.enabled },
            { "trigger", document.trigger ? FlowDetail::EncodeNode(*document.trigger) : FlowJson::Value(nullptr) },
            { "actions", std::move(actions) }
        };
        for (const auto& extension : document.extensions) {
            if (!FlowDetail::IsKnown(extension.first,
                { "format", "schemaVersion", "id", "revision", "name", "publisher", "description", "enabled", "trigger", "actions" }))
                root.push_back(extension);
        }

        std::string candidate;
        FlowJson::Error jsonError;
        if (!FlowJson::Stringify(FlowJson::Value(std::move(root)), candidate, jsonError, limits.json))
            return { FlowStorageError::schemaError, jsonError.message, "$" };
        FlowDocument validated;
        auto validation = ParseFlowJson(candidate, validated, limits);
        if (!validation) return validation;
        candidate.push_back('\n');
        json = std::move(candidate);
        return {};
    }

    class FlowStorage {
    public:
        explicit FlowStorage(FlowLimits limits = {}) : limits_(std::move(limits)) {}

        void SetBackupPath(std::filesystem::path path) {
            backupPath_ = std::move(path);
            hasCustomBackupPath_ = !backupPath_.empty();
        }
        const std::filesystem::path& Path() const noexcept { return path_; }
        bool IsLoaded() const noexcept { return loaded_; }

        FlowStorageResult Load(const std::filesystem::path& path, FlowDocument& output) {
            loaded_ = false;
            if (!FlowDetail::HasJsonExtension(path))
                return { FlowStorageError::invalidArgument, "flow files must use the .json extension" };
            path_ = path;
            if (!hasCustomBackupPath_) backupPath_ = std::filesystem::path(path.wstring() + L".bak");
            if (FlowDetail::SamePath(path_, backupPath_))
                return { FlowStorageError::invalidArgument,
                    "flow JSON primary and backup paths must be different" };
            const auto observedStamp = FlowDetail::GetFileStamp(path_);

            std::string bytes;
            auto read = FlowDetail::ReadFile(path_, bytes, limits_);
            if (read) {
                auto parsed = ParseFlowJson(bytes, output, limits_);
                if (parsed) {
                    const auto currentStamp = FlowDetail::GetFileStamp(path_);
                    if (!(currentStamp == observedStamp))
                        return { FlowStorageError::conflict,
                            "flow JSON file changed while it was being loaded" };
                    stamp_ = currentStamp;
                    loaded_ = true;
                    return {};
                }
                read = parsed;
            }

            std::error_code error;
            if ((read.error == FlowStorageError::parseError ||
                read.error == FlowStorageError::schemaError || read.error == FlowStorageError::ioError) &&
                std::filesystem::is_regular_file(backupPath_, error)) {
                std::string backupBytes;
                auto backupRead = FlowDetail::ReadFile(backupPath_, backupBytes, limits_);
                if (backupRead) {
                    FlowDocument recovered;
                    auto backupParsed = ParseFlowJson(backupBytes, recovered, limits_);
                    if (backupParsed) {
#ifdef _WIN32
                        FlowDetail::ProcessLock processLock(path_);
                        if (!processLock.locked())
                            return { FlowStorageError::busy,
                                "flow JSON file is busy in another process" };
#endif
                        if (!(FlowDetail::GetFileStamp(path_) == observedStamp))
                            return { FlowStorageError::conflict,
                                "flow JSON file changed while backup recovery was starting" };
                        static std::atomic<std::uint64_t> recoverySequence{ 0 };
#ifdef _WIN32
                        const auto corruptPath = std::filesystem::path(path_.wstring() +
                            L".corrupt." + std::to_wstring(GetCurrentProcessId()) + L"." +
                            std::to_wstring(++recoverySequence));
#else
                        const auto corruptPath = std::filesystem::path(path_.string() +
                            ".corrupt." + std::to_string(++recoverySequence));
#endif
                        auto restored = FlowDetail::WriteAtomic(
                            path_, corruptPath, backupBytes, observedStamp);
                        if (!restored) {
                            restored.message = "valid flow backup was found but the primary file could not be restored: " +
                                restored.message;
                            return restored;
                        }
                        output = std::move(recovered);
                        stamp_ = FlowDetail::GetFileStamp(path_);
                        loaded_ = true;
                        return { FlowStorageError::recoveredBackup,
                            "primary flow JSON was invalid; loaded the backup" };
                    }
                }
            }
            return read;
        }

        FlowStorageResult Save(const FlowDocument& document) {
            if (!loaded_ || path_.empty())
                return { FlowStorageError::notLoaded, "load a flow JSON file before saving" };
            if (!(FlowDetail::GetFileStamp(path_) == stamp_))
                return { FlowStorageError::conflict,
                    "flow JSON file changed externally; refusing to overwrite it" };
#ifdef _WIN32
            FlowDetail::ProcessLock processLock(path_);
            if (!processLock.locked())
                return { FlowStorageError::busy, "flow JSON file is busy in another process" };
            if (!(FlowDetail::GetFileStamp(path_) == stamp_))
                return { FlowStorageError::conflict,
                    "flow JSON file changed externally; refusing to overwrite it" };
#endif
            std::string json;
            auto serialized = SerializeFlowJson(document, json, limits_);
            if (!serialized) return serialized;
            auto written = FlowDetail::WriteAtomic(path_, backupPath_, json, stamp_);
            if (!written) return written;
            stamp_ = FlowDetail::GetFileStamp(path_);
            return {};
        }

        FlowStorageResult Create(const std::filesystem::path& path,
            const FlowDocument& document) {
            if (!FlowDetail::HasJsonExtension(path))
                return { FlowStorageError::invalidArgument, "flow files must use the .json extension" };
            path_ = path;
            if (!hasCustomBackupPath_) backupPath_ = std::filesystem::path(path.wstring() + L".bak");
            if (FlowDetail::SamePath(path_, backupPath_))
                return { FlowStorageError::invalidArgument,
                    "flow JSON primary and backup paths must be different" };
            std::string json;
            auto serialized = SerializeFlowJson(document, json, limits_);
            if (!serialized) return serialized;
#ifdef _WIN32
            FlowDetail::ProcessLock processLock(path_);
            if (!processLock.locked())
                return { FlowStorageError::busy, "flow JSON file is busy in another process" };
#endif
            std::error_code existsError;
            if (std::filesystem::is_regular_file(path_, existsError))
                return { FlowStorageError::conflict,
                    "flow JSON file already exists; load it before saving changes" };
            auto written = FlowDetail::WriteAtomic(path_, backupPath_, json,
                FlowDetail::FileStamp{});
            if (!written) return written;
            stamp_ = FlowDetail::GetFileStamp(path_);
            loaded_ = true;
            return {};
        }

    private:
        FlowLimits limits_;
        std::filesystem::path path_;
        std::filesystem::path backupPath_;
        FlowDetail::FileStamp stamp_{};
        bool loaded_ = false;
        bool hasCustomBackupPath_ = false;
    };

} // namespace PoggetCore::Storage
