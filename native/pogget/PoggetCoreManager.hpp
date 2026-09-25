#pragma once
#include <vector>
#include <cstdint>
#include <cwctype>
#include <memory>
#include <string>
#include <functional>
#include <set>
#include <map>
#include <algorithm>
#include <filesystem>
#include <cstdint>
#include <cwctype>

namespace PoggetCore {

    struct CoreContainerConfig {
        bool DisableLayoutAnimations = false;
        bool IsIntegrated = false;
        bool IsMergedHost = false;
        int SortMode = 0;
        int IconSpacingMode = 0;
        int IconSpacingType = 0;
        bool EnablePagedLayout = false;
        int PagedLayoutFlowMode = 0;
        int PagedLayoutMaxColumns = 4;
        int PagedLayoutMaxRows = 4;
        int PagedLayoutCurrentPage = 0;
        bool IsListView = false;
        bool IsInInlineFolderView = false;
        bool IsSearchMode = false;
        std::wstring searchText = L"";
        std::wstring title = L"";
        bool IsSectionCollapsed = false;
        bool ShowTextPreview = true;
        bool ShowMediaPreview = true;
        float textSize = 10.0f;
    };

    struct CoreIconLayoutData {
        int stableId = -1;
        const void* stableToken = nullptr;
        std::wstring path;
        std::wstring alias;
        std::wstring originalPath;
        std::wstring cachedName;
        uint64_t cachedTime = 0;
        bool isDirectory = false;
        uint64_t cachedSize = 0;
        std::wstring cachedExtension;
        std::wstring sectionTitle;
        void* originWindow = nullptr;
        void* originalIconPtr = nullptr; // back reference to IconData

        int originOrder = 0;

        float targetX = -100.0f;
        float targetY = -100.0f;
        float customWidth = -1.0f;
        float interactionClipLeftOffset = 0.0f;
        float interactionClipTopOffset = 0.0f;
        float interactionClipRightOffset = 0.0f;
        float interactionClipBottomOffset = 0.0f;
        bool hasInteractionClip = false;
        bool isCollapsed = false;
        bool isVisible = true;
        int pageIndex = 0;
    };

    struct CoreSectionHeaderLayoutData {
        void* originWindow = nullptr;
        std::wstring title;
        int x1 = 0;
        int y1 = 0;
        int x2 = 0;
        int y2 = 0;
        int pageIndex = 0;
        bool isContinuation = false;
        int continuationIndex = 0;
        int sectionPageIndex = 1;
        int sectionPageCount = 1;
    };

    struct CoreSectionLayoutInput {
        void* originWindow = nullptr;
        std::wstring title;
    };

    struct CoreLayoutAnchor {
        void* originWindow = nullptr;
        int iconId = -1;
        const void* iconToken = nullptr;
        std::wstring sectionTitle;
        int continuationIndex = 0;
        bool preferHeader = false;
    };

    struct CoreLayoutResult {
        bool isCustomLayout = false;
        bool isPaged = false;
        int flowMode = 0;
        int pageCount = 1;
        int currentPage = 0;
        int columnsPerPage = 1;
        int rowsPerPage = 1;
        bool anchorResolved = false;
        int resolvedAnchorPage = 0;
    };

    class PoggetCoreManager {
    public:
        PoggetCoreManager() = default;
        virtual ~PoggetCoreManager() = default;

        static bool MatchWildcard(const std::wstring& pattern, const std::wstring& text) {
            std::wstring p = pattern, t = text;
            std::transform(p.begin(), p.end(), p.begin(), ::towlower);
            std::transform(t.begin(), t.end(), t.begin(), ::towlower);

            size_t n = t.size(), m = p.size();
            size_t i = 0, j = 0;
            size_t startIndex = static_cast<size_t>(-1);
            size_t match = 0;

            while (i < n) {
                if (j < m && (p[j] == L'?' || p[j] == t[i])) {
                    i++; j++;
                } else if (j < m && p[j] == L'*') {
                    startIndex = j++;
                    match = i;
                } else if (startIndex != static_cast<size_t>(-1)) {
                    j = startIndex + 1;
                    i = ++match;
                } else {
                    return false;
                }
            }
            while (j < m && p[j] == L'*') j++;
            return j == m;
        }

        static void CalculatePositionsCore(
            std::vector<CoreIconLayoutData>& icons,
            std::vector<CoreSectionHeaderLayoutData>& outHeaders,
            void* containerWin,
            int containerWidth,
            int containerHeight,
            int startX,
            int startY,
            int iconSize,
            int gap,
            bool isSearchManager,
            bool isInlineManager,
            const std::function<CoreContainerConfig(void*)>& getConfig,
            const std::function<bool(void*, const std::wstring&)>& isSectionCollapsedInSearch,
            const std::vector<CoreSectionLayoutInput>* sectionInputs = nullptr,
            const CoreLayoutAnchor* layoutAnchor = nullptr,
            CoreLayoutResult* outResult = nullptr
        );

        static void CalculatePositionsCore(
            std::vector<CoreIconLayoutData>& icons,
            std::vector<CoreSectionHeaderLayoutData>& outHeaders,
            void* containerWin,
            int containerWidth,
            int startX,
            int startY,
            int iconSize,
            int gap,
            bool isSearchManager,
            bool isInlineManager,
            const std::function<CoreContainerConfig(void*)>& getConfig,
            const std::function<bool(void*, const std::wstring&)>& isSectionCollapsedInSearch
        ) {
            CalculatePositionsCore(icons, outHeaders, containerWin,
                containerWidth, 0, startX, startY, iconSize, gap,
                isSearchManager, isInlineManager, getConfig,
                isSectionCollapsedInSearch, nullptr, nullptr, nullptr);
        }
    };

}
