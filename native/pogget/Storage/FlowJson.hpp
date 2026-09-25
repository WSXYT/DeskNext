#pragma once

#include <charconv>
#include <cmath>
#include <cstdint>
#include <iomanip>
#include <limits>
#include <locale>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <variant>
#include <vector>
#include <unordered_set>

namespace PoggetCore::Storage::FlowJson {

    struct Value {
        using Array = std::vector<Value>;
        using Object = std::vector<std::pair<std::string, Value>>;
        using Data = std::variant<std::nullptr_t, bool, std::int64_t, double,
            std::string, Array, Object>;

        Data data = nullptr;

        Value() = default;
        Value(std::nullptr_t) : data(nullptr) {}
        Value(bool value) : data(value) {}
        Value(std::int64_t value) : data(value) {}
        Value(double value) : data(value) {}
        Value(std::string value) : data(std::move(value)) {}
        Value(const char* value) : data(std::string(value ? value : "")) {}
        Value(Array value) : data(std::move(value)) {}
        Value(Object value) : data(std::move(value)) {}

        template<typename T> const T* get_if() const noexcept {
            return std::get_if<T>(&data);
        }
        template<typename T> T* get_if() noexcept {
            return std::get_if<T>(&data);
        }
    };

    inline const Value* Find(const Value::Object& object, std::string_view key) noexcept {
        for (const auto& member : object) {
            if (member.first == key) return &member.second;
        }
        return nullptr;
    }

    inline bool Contains(const Value::Object& object, std::string_view key) noexcept {
        return Find(object, key) != nullptr;
    }

    struct Limits {
        std::size_t maxInputBytes = 8ULL * 1024 * 1024;
        std::size_t maxDepth = 64;
        std::size_t maxValues = 100'000;
        std::size_t maxStringBytes = 1024 * 1024;
        std::size_t maxObjectMembers = 50'000;
        std::size_t maxArrayItems = 50'000;
    };

    struct Error {
        std::string message;
        std::size_t offset = 0;
        std::size_t line = 1;
        std::size_t column = 1;

        explicit operator bool() const noexcept { return !message.empty(); }
    };

    namespace Detail {

        inline bool IsContinuation(unsigned char value) noexcept {
            return (value & 0xC0U) == 0x80U;
        }

        inline bool IsValidUtf8(std::string_view text) noexcept {
            std::size_t i = 0;
            while (i < text.size()) {
                const auto first = static_cast<unsigned char>(text[i]);
                if (first <= 0x7F) {
                    ++i;
                    continue;
                }
                if (first >= 0xC2 && first <= 0xDF) {
                    if (i + 1 >= text.size() ||
                        !IsContinuation(static_cast<unsigned char>(text[i + 1]))) return false;
                    i += 2;
                    continue;
                }
                if (first >= 0xE0 && first <= 0xEF) {
                    if (i + 2 >= text.size()) return false;
                    const auto second = static_cast<unsigned char>(text[i + 1]);
                    const auto third = static_cast<unsigned char>(text[i + 2]);
                    if (!IsContinuation(second) || !IsContinuation(third) ||
                        (first == 0xE0 && second < 0xA0) ||
                        (first == 0xED && second >= 0xA0)) return false;
                    i += 3;
                    continue;
                }
                if (first >= 0xF0 && first <= 0xF4) {
                    if (i + 3 >= text.size()) return false;
                    const auto second = static_cast<unsigned char>(text[i + 1]);
                    if (!IsContinuation(second) ||
                        !IsContinuation(static_cast<unsigned char>(text[i + 2])) ||
                        !IsContinuation(static_cast<unsigned char>(text[i + 3])) ||
                        (first == 0xF0 && second < 0x90) ||
                        (first == 0xF4 && second >= 0x90)) return false;
                    i += 4;
                    continue;
                }
                return false;
            }
            return true;
        }

        inline void AppendUtf8(std::string& output, std::uint32_t codePoint) {
            if (codePoint <= 0x7F) {
                output.push_back(static_cast<char>(codePoint));
            }
            else if (codePoint <= 0x7FF) {
                output.push_back(static_cast<char>(0xC0U | (codePoint >> 6)));
                output.push_back(static_cast<char>(0x80U | (codePoint & 0x3FU)));
            }
            else if (codePoint <= 0xFFFF) {
                output.push_back(static_cast<char>(0xE0U | (codePoint >> 12)));
                output.push_back(static_cast<char>(0x80U | ((codePoint >> 6) & 0x3FU)));
                output.push_back(static_cast<char>(0x80U | (codePoint & 0x3FU)));
            }
            else {
                output.push_back(static_cast<char>(0xF0U | (codePoint >> 18)));
                output.push_back(static_cast<char>(0x80U | ((codePoint >> 12) & 0x3FU)));
                output.push_back(static_cast<char>(0x80U | ((codePoint >> 6) & 0x3FU)));
                output.push_back(static_cast<char>(0x80U | (codePoint & 0x3FU)));
            }
        }

        class Parser {
        public:
            Parser(std::string_view input, Limits limits)
                : input_(input), limits_(limits) {}

            bool Parse(Value& output, Error& error) {
                if (limits_.maxInputBytes != 0 && input_.size() > limits_.maxInputBytes) {
                    return Fail("JSON input exceeds the byte limit", error);
                }
                if (!IsValidUtf8(input_)) return Fail("JSON input is not valid UTF-8", error);
                SkipWhitespace();
                if (!ParseValue(output, 1, error)) return false;
                SkipWhitespace();
                if (position_ != input_.size()) return Fail("unexpected data after JSON value", error);
                return true;
            }

        private:
            std::string_view input_;
            Limits limits_;
            std::size_t position_ = 0;
            std::size_t valueCount_ = 0;

            bool Fail(const char* message, Error& error) const {
                error.message = message;
                error.offset = position_;
                error.line = 1;
                error.column = 1;
                for (std::size_t i = 0; i < position_ && i < input_.size(); ++i) {
                    if (input_[i] == '\n') {
                        ++error.line;
                        error.column = 1;
                    }
                    else {
                        ++error.column;
                    }
                }
                return false;
            }

            void SkipWhitespace() noexcept {
                while (position_ < input_.size()) {
                    const char c = input_[position_];
                    if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
                    ++position_;
                }
            }

            bool ParseValue(Value& output, std::size_t depth, Error& error) {
                if (limits_.maxDepth != 0 && depth > limits_.maxDepth)
                    return Fail("JSON nesting exceeds the depth limit", error);
                if (limits_.maxValues != 0 && ++valueCount_ > limits_.maxValues)
                    return Fail("JSON value count exceeds the limit", error);
                if (position_ >= input_.size()) return Fail("expected a JSON value", error);

                switch (input_[position_]) {
                case 'n': return ParseLiteral("null", Value(nullptr), output, error);
                case 't': return ParseLiteral("true", Value(true), output, error);
                case 'f': return ParseLiteral("false", Value(false), output, error);
                case '"': {
                    std::string text;
                    if (!ParseString(text, error)) return false;
                    output = Value(std::move(text));
                    return true;
                }
                case '[': return ParseArray(output, depth, error);
                case '{': return ParseObject(output, depth, error);
                default:
                    if (input_[position_] == '-' ||
                        (input_[position_] >= '0' && input_[position_] <= '9'))
                        return ParseNumber(output, error);
                    return Fail("invalid JSON value", error);
                }
            }

            bool ParseLiteral(std::string_view literal, Value value, Value& output, Error& error) {
                if (input_.substr(position_, literal.size()) != literal)
                    return Fail("invalid JSON literal", error);
                position_ += literal.size();
                output = std::move(value);
                return true;
            }

            static int HexDigit(char c) noexcept {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            }

            bool ParseHex4(std::uint32_t& value, Error& error) {
                if (position_ + 4 > input_.size()) return Fail("incomplete Unicode escape", error);
                value = 0;
                for (int i = 0; i < 4; ++i) {
                    const int digit = HexDigit(input_[position_++]);
                    if (digit < 0) return Fail("invalid Unicode escape", error);
                    value = value * 16U + static_cast<std::uint32_t>(digit);
                }
                return true;
            }

            bool ParseString(std::string& output, Error& error) {
                ++position_;
                output.clear();
                while (position_ < input_.size()) {
                    const auto c = static_cast<unsigned char>(input_[position_++]);
                    if (c == '"') return true;
                    if (c < 0x20) return Fail("unescaped control character in JSON string", error);
                    if (c != '\\') {
                        output.push_back(static_cast<char>(c));
                    }
                    else {
                        if (position_ >= input_.size()) return Fail("incomplete JSON escape", error);
                        const char escaped = input_[position_++];
                        switch (escaped) {
                        case '"': output.push_back('"'); break;
                        case '\\': output.push_back('\\'); break;
                        case '/': output.push_back('/'); break;
                        case 'b': output.push_back('\b'); break;
                        case 'f': output.push_back('\f'); break;
                        case 'n': output.push_back('\n'); break;
                        case 'r': output.push_back('\r'); break;
                        case 't': output.push_back('\t'); break;
                        case 'u': {
                            std::uint32_t first = 0;
                            if (!ParseHex4(first, error)) return false;
                            if (first >= 0xD800 && first <= 0xDBFF) {
                                if (position_ + 2 > input_.size() || input_[position_] != '\\' ||
                                    input_[position_ + 1] != 'u')
                                    return Fail("high surrogate is not followed by a low surrogate", error);
                                position_ += 2;
                                std::uint32_t second = 0;
                                if (!ParseHex4(second, error)) return false;
                                if (second < 0xDC00 || second > 0xDFFF)
                                    return Fail("invalid low surrogate", error);
                                AppendUtf8(output, 0x10000U + ((first - 0xD800U) << 10U) +
                                    (second - 0xDC00U));
                            }
                            else if (first >= 0xDC00 && first <= 0xDFFF) {
                                return Fail("unexpected low surrogate", error);
                            }
                            else {
                                AppendUtf8(output, first);
                            }
                            break;
                        }
                        default: return Fail("invalid JSON escape", error);
                        }
                    }
                    if (limits_.maxStringBytes != 0 && output.size() > limits_.maxStringBytes)
                        return Fail("JSON string exceeds the byte limit", error);
                }
                return Fail("unterminated JSON string", error);
            }

            bool ParseArray(Value& output, std::size_t depth, Error& error) {
                ++position_;
                SkipWhitespace();
                Value::Array array;
                if (position_ < input_.size() && input_[position_] == ']') {
                    ++position_;
                    output = Value(std::move(array));
                    return true;
                }
                while (true) {
                    if (limits_.maxArrayItems != 0 && array.size() >= limits_.maxArrayItems)
                        return Fail("JSON array exceeds the item limit", error);
                    Value item;
                    if (!ParseValue(item, depth + 1, error)) return false;
                    array.push_back(std::move(item));
                    SkipWhitespace();
                    if (position_ >= input_.size()) return Fail("unterminated JSON array", error);
                    if (input_[position_] == ']') {
                        ++position_;
                        output = Value(std::move(array));
                        return true;
                    }
                    if (input_[position_++] != ',') return Fail("expected ',' or ']' in JSON array", error);
                    SkipWhitespace();
                    if (position_ < input_.size() && input_[position_] == ']')
                        return Fail("trailing comma in JSON array", error);
                }
            }

            bool ParseObject(Value& output, std::size_t depth, Error& error) {
                ++position_;
                SkipWhitespace();
                Value::Object object;
                std::unordered_set<std::string> keys;
                if (position_ < input_.size() && input_[position_] == '}') {
                    ++position_;
                    output = Value(std::move(object));
                    return true;
                }
                while (true) {
                    if (limits_.maxObjectMembers != 0 && object.size() >= limits_.maxObjectMembers)
                        return Fail("JSON object exceeds the member limit", error);
                    if (position_ >= input_.size() || input_[position_] != '"')
                        return Fail("expected a quoted JSON member name", error);
                    std::string key;
                    if (!ParseString(key, error)) return false;
                    if (!keys.emplace(key).second) return Fail("duplicate JSON member name", error);
                    SkipWhitespace();
                    if (position_ >= input_.size() || input_[position_++] != ':')
                        return Fail("expected ':' after JSON member name", error);
                    SkipWhitespace();
                    Value value;
                    if (!ParseValue(value, depth + 1, error)) return false;
                    object.emplace_back(std::move(key), std::move(value));
                    SkipWhitespace();
                    if (position_ >= input_.size()) return Fail("unterminated JSON object", error);
                    if (input_[position_] == '}') {
                        ++position_;
                        output = Value(std::move(object));
                        return true;
                    }
                    if (input_[position_++] != ',') return Fail("expected ',' or '}' in JSON object", error);
                    SkipWhitespace();
                    if (position_ < input_.size() && input_[position_] == '}')
                        return Fail("trailing comma in JSON object", error);
                }
            }

            bool ParseNumber(Value& output, Error& error) {
                const std::size_t start = position_;
                if (input_[position_] == '-') ++position_;
                if (position_ >= input_.size()) return Fail("incomplete JSON number", error);
                if (input_[position_] == '0') {
                    ++position_;
                    if (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9')
                        return Fail("leading zero in JSON number", error);
                }
                else {
                    if (input_[position_] < '1' || input_[position_] > '9')
                        return Fail("invalid JSON number", error);
                    while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9')
                        ++position_;
                }

                bool isInteger = true;
                if (position_ < input_.size() && input_[position_] == '.') {
                    isInteger = false;
                    ++position_;
                    const std::size_t digits = position_;
                    while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9')
                        ++position_;
                    if (digits == position_) return Fail("missing fraction digits in JSON number", error);
                }
                if (position_ < input_.size() && (input_[position_] == 'e' || input_[position_] == 'E')) {
                    isInteger = false;
                    ++position_;
                    if (position_ < input_.size() && (input_[position_] == '+' || input_[position_] == '-'))
                        ++position_;
                    const std::size_t digits = position_;
                    while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9')
                        ++position_;
                    if (digits == position_) return Fail("missing exponent digits in JSON number", error);
                }

                const std::string_view token = input_.substr(start, position_ - start);
                if (isInteger) {
                    std::int64_t integer = 0;
                    const auto parsed = std::from_chars(token.data(), token.data() + token.size(), integer);
                    if (parsed.ec != std::errc{} || parsed.ptr != token.data() + token.size())
                        return Fail("JSON integer is outside the signed 64-bit range", error);
                    output = Value(integer);
                    return true;
                }

                double decimal = 0.0;
#if defined(__APPLE__)
                // Apple libc++ does not yet provide floating-point from_chars.
                std::istringstream stream{std::string(token)};
                stream.imbue(std::locale::classic());
                stream >> std::noskipws >> decimal;
                if (!stream || !stream.eof() || !std::isfinite(decimal))
                    return Fail("JSON number is outside the finite double range", error);
#else
                const auto parsed = std::from_chars(token.data(), token.data() + token.size(),
                    decimal, std::chars_format::general);
                if (parsed.ec != std::errc{} || parsed.ptr != token.data() + token.size() ||
                    !std::isfinite(decimal))
                    return Fail("JSON number is outside the finite double range", error);
#endif
                output = Value(decimal);
                return true;
            }
        };

        inline void AppendEscaped(std::string_view value, std::string& output) {
            static constexpr char hex[] = "0123456789abcdef";
            output.push_back('"');
            for (const unsigned char c : value) {
                switch (c) {
                case '"': output += "\\\""; break;
                case '\\': output += "\\\\"; break;
                case '\b': output += "\\b"; break;
                case '\f': output += "\\f"; break;
                case '\n': output += "\\n"; break;
                case '\r': output += "\\r"; break;
                case '\t': output += "\\t"; break;
                default:
                    if (c < 0x20) {
                        output += "\\u00";
                        output.push_back(hex[c >> 4]);
                        output.push_back(hex[c & 0x0F]);
                    }
                    else output.push_back(static_cast<char>(c));
                }
            }
            output.push_back('"');
        }

        inline bool WriteValue(const Value& value, std::string& output, std::size_t depth,
            const Limits& limits, Error& error) {
            if (limits.maxDepth != 0 && depth > limits.maxDepth) {
                error.message = "JSON output exceeds the depth limit";
                return false;
            }
            if (std::holds_alternative<std::nullptr_t>(value.data)) output += "null";
            else if (const auto* boolean = value.get_if<bool>()) output += *boolean ? "true" : "false";
            else if (const auto* integer = value.get_if<std::int64_t>()) output += std::to_string(*integer);
            else if (const auto* decimal = value.get_if<double>()) {
                if (!std::isfinite(*decimal)) {
                    error.message = "JSON cannot encode a non-finite number";
                    return false;
                }
#if defined(__APPLE__)
                // Classic locale prevents user locale decimal commas in JSON.
                std::ostringstream stream;
                stream.imbue(std::locale::classic());
                stream << std::setprecision(std::numeric_limits<double>::max_digits10) << *decimal;
                if (!stream) {
                    error.message = "could not encode JSON number";
                    return false;
                }
                const std::string token = stream.str();
#else
                char buffer[64]{};
                const auto result = std::to_chars(buffer, buffer + sizeof(buffer), *decimal,
                    std::chars_format::general);
                if (result.ec != std::errc{}) {
                    error.message = "could not encode JSON number";
                    return false;
                }
                const std::string_view token(buffer,
                    static_cast<std::size_t>(result.ptr - buffer));
#endif
                output.append(token);
                if (token.find_first_of(".eE") == std::string_view::npos) output += ".0";
            }
            else if (const auto* text = value.get_if<std::string>()) {
                if (!IsValidUtf8(*text)) {
                    error.message = "JSON string is not valid UTF-8";
                    return false;
                }
                AppendEscaped(*text, output);
            }
            else if (const auto* array = value.get_if<Value::Array>()) {
                output.push_back('[');
                for (std::size_t i = 0; i < array->size(); ++i) {
                    if (i != 0) output.push_back(',');
                    if (!WriteValue((*array)[i], output, depth + 1, limits, error)) return false;
                }
                output.push_back(']');
            }
            else if (const auto* object = value.get_if<Value::Object>()) {
                output.push_back('{');
                for (std::size_t i = 0; i < object->size(); ++i) {
                    if (i != 0) output.push_back(',');
                    if (!IsValidUtf8((*object)[i].first)) {
                        error.message = "JSON member name is not valid UTF-8";
                        return false;
                    }
                    AppendEscaped((*object)[i].first, output);
                    output.push_back(':');
                    if (!WriteValue((*object)[i].second, output, depth + 1, limits, error)) return false;
                }
                output.push_back('}');
            }
            return true;
        }
    }

    inline bool Parse(std::string_view input, Value& output, Error& error,
        Limits limits = {}) {
        error = {};
        return Detail::Parser(input, limits).Parse(output, error);
    }

    inline bool Stringify(const Value& value, std::string& output, Error& error,
        Limits limits = {}) {
        output.clear();
        error = {};
        return Detail::WriteValue(value, output, 1, limits, error);
    }

} // namespace PoggetCore::Storage::FlowJson
