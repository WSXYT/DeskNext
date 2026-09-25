#include "PoggetCoreManager.hpp"
#include <cmath>

namespace PoggetCore {

    void PoggetCoreManager::CalculatePositionsCore(
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
        const std::vector<CoreSectionLayoutInput>* sectionInputs,
        const CoreLayoutAnchor* layoutAnchor,
        CoreLayoutResult* outResult
    ) {
        CoreLayoutResult layoutResult;
        if (outResult) *outResult = layoutResult;

        CoreContainerConfig mainData = getConfig(containerWin);
        bool needsSort = false;
        bool isIntegrated = mainData.IsIntegrated || isSearchManager;
        auto showTextPreviewFor = [&](void* targetWin) {
            if (mainData.IsIntegrated && !isSearchManager && !isInlineManager) {
                return mainData.ShowTextPreview;
            }
            return getConfig(targetWin).ShowTextPreview;
        };

        if (isIntegrated && !isInlineManager && !isSearchManager) {
            needsSort = true;
        } else if (mainData.SortMode != 0 && !isSearchManager) {
            needsSort = true;
        }

        if (needsSort && !icons.empty()) {
            std::map<void*, std::wstring> sectionTitles;
            std::map<void*, int> originOrder;
            int currentOrder = 0;
			if (sectionInputs) {
				for (const auto& section : *sectionInputs) {
					if (section.originWindow &&
						originOrder.find(section.originWindow) == originOrder.end()) {
						originOrder[section.originWindow] = currentOrder++;
					}
				}
			}

            for (auto& ic : icons) {
                void* targetWin = ic.originWindow ? ic.originWindow : containerWin;
                if (!(isSearchManager && mainData.IsInInlineFolderView) && !ic.sectionTitle.empty()) {
                    sectionTitles[targetWin] = ic.sectionTitle;
                    ic.sectionTitle = L"";
                }
                if (originOrder.find(targetWin) == originOrder.end()) {
                    originOrder[targetWin] = currentOrder++;
                }
            }

            std::stable_sort(icons.begin(), icons.end(), [&originOrder, containerWin, &getConfig,
                &mainData, isIntegrated, isSearchManager](const CoreIconLayoutData& a, const CoreIconLayoutData& b) {
                void* aWin = a.originWindow ? a.originWindow : containerWin;
                void* bWin = b.originWindow ? b.originWindow : containerWin;

                if (aWin != bWin) {
                    int orderA = originOrder.count(aWin) ? originOrder.at(aWin) : 0;
                    int orderB = originOrder.count(bWin) ? originOrder.at(bWin) : 0;
                    return orderA < orderB;
                }

                int mode = (isIntegrated && !isSearchManager)
                    ? mainData.SortMode
                    : getConfig(aWin).SortMode;

                if (mode == 1) return a.cachedName < b.cachedName;
                if (mode == 2) return a.cachedName > b.cachedName;
                if (mode == 3) return a.cachedTime < b.cachedTime;
                if (mode == 4) return a.cachedTime > b.cachedTime;
                if (mode == 5) {
                    if (a.isDirectory != b.isDirectory) return a.isDirectory > b.isDirectory;
                    if (a.cachedExtension != b.cachedExtension) return a.cachedExtension < b.cachedExtension;
                    return a.cachedName < b.cachedName;
                }
                if (mode == 6) {
                    if (a.isDirectory != b.isDirectory) return a.isDirectory > b.isDirectory;
                    if (a.cachedExtension != b.cachedExtension) return a.cachedExtension > b.cachedExtension;
                    return a.cachedName > b.cachedName;
                }
                if (mode == 7) {
                    if (a.isDirectory != b.isDirectory) return a.isDirectory > b.isDirectory;
                    if (a.cachedSize != b.cachedSize) return a.cachedSize < b.cachedSize;
                    return a.cachedName < b.cachedName;
                }
                if (mode == 8) {
                    if (a.isDirectory != b.isDirectory) return a.isDirectory > b.isDirectory;
                    if (a.cachedSize != b.cachedSize) return a.cachedSize > b.cachedSize;
                    return a.cachedName > b.cachedName;
                }
                return false;
            });

            std::set<void*> processedOrigins;
            for (auto& ic : icons) {
                void* targetWin = ic.originWindow ? ic.originWindow : containerWin;
                if (processedOrigins.find(targetWin) == processedOrigins.end()) {
                    if (!(isSearchManager && mainData.IsInInlineFolderView) && sectionTitles.find(targetWin) != sectionTitles.end()) {
                        ic.sectionTitle = sectionTitles[targetWin];
                    }
                    processedOrigins.insert(targetWin);
                }
            }
        }

        const bool customLayout = mainData.EnablePagedLayout &&
            (!mainData.IsIntegrated || mainData.IsMergedHost) &&
            !mainData.IsListView &&
            !mainData.IsSearchMode && !mainData.IsInInlineFolderView &&
            !isSearchManager && !isInlineManager;
        const int customFlowMode = std::clamp(mainData.PagedLayoutFlowMode, 0, 2);
        const bool pagedLayout = customLayout && customFlowMode != 2;
        const bool horizontalPages = customFlowMode == 0;
        layoutResult.isCustomLayout = customLayout;
        layoutResult.flowMode = customFlowMode;
        int spacingMode = mainData.IconSpacingMode == 3 ? 1 : mainData.IconSpacingMode;
        int actualStartX = startX;
        if (mainData.IconSpacingType == 1) {
            actualStartX = (spacingMode == 0) ? 16 : ((spacingMode == 1) ? 24 : 32);
        }
        int availableWidth = (std::max)(0, containerWidth - 2 * actualStartX);
        if (pagedLayout && !horizontalPages) {
            availableWidth = (std::max)(0, availableWidth - 18);
        }
        bool isList = mainData.IsListView;

        int gapX = gap + (spacingMode == 1 ? 16 : (spacingMode == 2 ? 36 : 0));
        if (mainData.textSize > 12.0f && mainData.textSize < 100.0f) {
            float scaleDiff = (mainData.textSize - 12.0f);
            gapX += static_cast<int>(std::round(scaleDiff * 4.0f));
        }
        // 文本预览横跨两个标准图标单元；宽度必须包含两个单元之间的动态间隔，
        // 才能保证它左右两侧与普通图标保持相同留白。
        const int textWidgetWidth = iconSize * 2 + gapX;
        const int layoutTextWidgetWidth = customLayout
            ? (std::min)(textWidgetWidth, (std::max)(1, availableWidth))
            : textWidgetWidth;
        int gapY = gap + 15 + (spacingMode == 1 ? 20 : (spacingMode == 2 ? 45 : 0));
        int baseListStepY = (iconSize / 2) + 8;
        int listStepY = baseListStepY + (spacingMode == 1 ? 12 : (spacingMode == 2 ? 28 : 0));

        int iconsPerRow = isList ? 1 : (availableWidth / (iconSize + gapX));
        if (iconsPerRow <= 0) iconsPerRow = 1;
        if (customLayout) {
            iconsPerRow = (std::min)(iconsPerRow,
                (std::max)(1, mainData.PagedLayoutMaxColumns));
        }
        const auto getItemSpan = [iconsPerRow](bool isTextWidget) -> int {
            return isTextWidget ? (std::min)(2, iconsPerRow) : 1;
        };

        if (mainData.IconSpacingType == 1 && !isList) {
            if (iconsPerRow > 1) {
                gapX = (availableWidth - iconsPerRow * iconSize) / (iconsPerRow - 1);
            }
        }

        int usedWidth = isList ? availableWidth : (iconsPerRow * (iconSize + gapX) - gapX);
        int centeredOffsetX = isList ? 0 : (mainData.IconSpacingType == 1 ? 0 : (availableWidth - usedWidth) / 2);

        float standardRowGapX = static_cast<float>(gapX);
        float standardRowCenteredOffsetX = static_cast<float>(centeredOffsetX);
        if (mainData.IconSpacingType == 1 && !isList) {
            int standardNumItems = iconsPerRow;
            int standardTotalItemsWidth = standardNumItems * iconSize;
            float hoverExpandX = (spacingMode == 1) ? 8.0f : ((spacingMode == 2) ? 18.0f : 0.0f);
            int E = 5 + static_cast<int>(hoverExpandX);
            if (standardNumItems > 1) {
                int totalCollisionWidth = standardTotalItemsWidth + standardNumItems * (2 * E);
                int remainingSpace = availableWidth - totalCollisionWidth;
                if (remainingSpace >= 0) {
                    float hoverGapX = static_cast<float>(remainingSpace) / (standardNumItems - 1);
                    standardRowGapX = hoverGapX + 2.0f * E;
                    standardRowCenteredOffsetX = static_cast<float>(E);
                } else {
                    int spacingSpace = availableWidth - standardTotalItemsWidth;
                    if (spacingSpace < 0) spacingSpace = 0;
                    float unit = static_cast<float>(spacingSpace) / (2.0f * standardNumItems);
                    standardRowCenteredOffsetX = unit;
                    float totalGapsSpace = static_cast<float>(spacingSpace) - 2.0f * standardRowCenteredOffsetX;
                    if (totalGapsSpace < 0.0f) totalGapsSpace = 0.0f;
                    standardRowGapX = totalGapsSpace / (standardNumItems - 1);
                }
            } else {
                standardRowGapX = 0.0f;
                standardRowCenteredOffsetX = static_cast<float>(availableWidth - standardTotalItemsWidth) / 2.0f;
                if (standardRowCenteredOffsetX < 0.0f) standardRowCenteredOffsetX = 0.0f;
            }
        }

        for (size_t i = 0; i < icons.size(); ++i) {
            icons[i].isVisible = true;
            icons[i].isCollapsed = false;
            void* targetWin = icons[i].originWindow ? icons[i].originWindow : containerWin;

            if (!isSearchManager && mainData.IsSearchMode && !mainData.searchText.empty()) {
                std::wstring name = icons[i].alias == L"vui!NULL" ? std::filesystem::path(icons[i].path).filename().wstring() : icons[i].alias;
                bool isOriginalLnk = false;
                std::wstring pathExt = std::filesystem::path(icons[i].path).extension().wstring();
                std::transform(pathExt.begin(), pathExt.end(), pathExt.begin(), ::towlower);
                if (pathExt == L".lnk") {
                    isOriginalLnk = true;
                } else if (icons[i].originalPath != L"vui!NULL" && !icons[i].originalPath.empty()) {
                    std::wstring origExt = std::filesystem::path(icons[i].originalPath).extension().wstring();
                    std::transform(origExt.begin(), origExt.end(), origExt.begin(), ::towlower);
                    if (origExt == L".lnk") isOriginalLnk = true;
                }
                if (isOriginalLnk) {
                    bool aliasContainsLnk = false;
                    if (icons[i].alias != L"vui!NULL") {
                        std::wstring aliasLower = icons[i].alias;
                        std::transform(aliasLower.begin(), aliasLower.end(), aliasLower.begin(), ::towlower);
                        if (aliasLower.find(L".lnk") != std::wstring::npos) aliasContainsLnk = true;
                    }
                    if (!aliasContainsLnk) {
                        size_t lnkPos = name.size() >= 4 ? name.size() - 4 : std::wstring::npos;
                        if (lnkPos != std::wstring::npos) {
                            std::wstring tail = name.substr(lnkPos);
                            std::transform(tail.begin(), tail.end(), tail.begin(), ::towlower);
                            if (tail == L".lnk") name = name.substr(0, lnkPos);
                        }
                    }
                }
                if (!MatchWildcard(mainData.searchText, name)) {
                    icons[i].isVisible = false;
                }
            }
        }

        struct Row {
            std::vector<size_t> iconIndices;
            void* targetWin = nullptr;
            std::wstring sectionTitle = L"";
            bool isFull = false;
        };
        std::vector<Row> layoutRows;
        std::vector<size_t> iconToRowMap(icons.size(), static_cast<size_t>(-1));

        if (mainData.IconSpacingType == 1 && !isList) {
            void* tempLastOrigin = nullptr;
            std::wstring activeSectionTitle = L"";
            Row currentTempRow;
            float hoverExpandX = (spacingMode == 1) ? 8.0f : ((spacingMode == 2) ? 18.0f : 0.0f);
            int E = 5 + static_cast<int>(hoverExpandX);

            for (size_t i = 0; i < icons.size(); ++i) {
                if (!icons[i].isVisible) continue;
                void* targetWin = icons[i].originWindow ? icons[i].originWindow : containerWin;
                
                CoreContainerConfig targetData = getConfig(targetWin);

                bool sectionChanged = (targetWin != tempLastOrigin || (isSearchManager && mainData.IsInInlineFolderView && !icons[i].sectionTitle.empty()));
                if (sectionChanged) {
                    if (!currentTempRow.iconIndices.empty()) {
                        currentTempRow.isFull = false;
                        layoutRows.push_back(currentTempRow);
                        currentTempRow.iconIndices.clear();
                        currentTempRow.isFull = false;
                    }
                    tempLastOrigin = targetWin;
                    activeSectionTitle = icons[i].sectionTitle.empty() ? targetData.title : icons[i].sectionTitle;
                } else if (!icons[i].sectionTitle.empty()) {
                    activeSectionTitle = icons[i].sectionTitle;
                }

                bool sectionCollapsed = false;
                if (isSearchManager) sectionCollapsed = isSectionCollapsedInSearch(targetWin, activeSectionTitle);
                else sectionCollapsed = (isIntegrated && !isInlineManager) ? targetData.IsSectionCollapsed : false;
                if (sectionCollapsed) {
                    icons[i].isCollapsed = true;
                    continue;
                }

                currentTempRow.targetWin = targetWin;
                currentTempRow.sectionTitle = activeSectionTitle;

                std::vector<size_t> testIndices = currentTempRow.iconIndices;
                testIndices.push_back(i);
                int testNumItems = static_cast<int>(testIndices.size());
                int testSpan = 0;
                int minRequiredWidth = 0;
                for (size_t idx : testIndices) {
                    void* tWin = icons[idx].originWindow ? icons[idx].originWindow : containerWin;
                    bool isTxt = (showTextPreviewFor(tWin) && (MatchWildcard(L"*.txt", icons[idx].path) || MatchWildcard(L"*.md", icons[idx].path)));
                    if (isTxt) {
                        testSpan += getItemSpan(true);
                        int w_base = layoutTextWidgetWidth;
                        int maxShrink = w_base / 6;
                        minRequiredWidth += (w_base - maxShrink);
                    } else {
                        testSpan += 1;
                        minRequiredWidth += iconSize;
                    }
                }
                int min_padding = 10;
                int min_gap = 16;
                if (spacingMode == 0) { min_padding = 4; min_gap = 8; }
                else if (spacingMode == 2) { min_padding = 18; min_gap = 28; }

                minRequiredWidth += 2 * min_padding;
                if (testNumItems > 1) {
                    minRequiredWidth += (testNumItems - 1) * min_gap;
                }

                if ((minRequiredWidth > availableWidth ||
                    (customLayout && testSpan > iconsPerRow)) &&
                    !currentTempRow.iconIndices.empty()) {
                    currentTempRow.isFull = true;
                    layoutRows.push_back(currentTempRow);
                    currentTempRow.iconIndices.clear();
                    currentTempRow.isFull = false;
                }
                currentTempRow.iconIndices.push_back(i);
                iconToRowMap[i] = layoutRows.size();
            }
            if (!currentTempRow.iconIndices.empty()) {
                currentTempRow.isFull = false;
                layoutRows.push_back(currentTempRow);
            }
        } else {
            int tempCursorX = 0;
            void* tempLastOrigin = nullptr;
            std::wstring activeSectionTitle = L"";
            Row currentTempRow;

            for (size_t i = 0; i < icons.size(); ++i) {
                if (!icons[i].isVisible) continue;
                void* targetWin = icons[i].originWindow ? icons[i].originWindow : containerWin;

                CoreContainerConfig targetData = getConfig(targetWin);

                bool sectionChanged = (targetWin != tempLastOrigin || (isSearchManager && mainData.IsInInlineFolderView && !icons[i].sectionTitle.empty()));
                if (sectionChanged) {
                    if (!currentTempRow.iconIndices.empty()) {
                        layoutRows.push_back(currentTempRow);
                        currentTempRow.iconIndices.clear();
                    }
                    tempCursorX = 0;
                    tempLastOrigin = targetWin;
                    activeSectionTitle = icons[i].sectionTitle.empty() ? targetData.title : icons[i].sectionTitle;
                } else if (!icons[i].sectionTitle.empty()) {
                    activeSectionTitle = icons[i].sectionTitle;
                }

                bool sectionCollapsed = false;
                if (isSearchManager) sectionCollapsed = isSectionCollapsedInSearch(targetWin, activeSectionTitle);
                else sectionCollapsed = (isIntegrated && !isInlineManager) ? targetData.IsSectionCollapsed : false;
                if (sectionCollapsed) {
                    icons[i].isCollapsed = true;
                    continue;
                }

                bool isTxt = (!isList && showTextPreviewFor(targetWin) && (MatchWildcard(L"*.txt", icons[i].path) || MatchWildcard(L"*.md", icons[i].path)));
                int itemSpan = getItemSpan(isTxt);
                if (tempCursorX + itemSpan > iconsPerRow && tempCursorX > 0) {
                    if (!currentTempRow.iconIndices.empty()) {
                        layoutRows.push_back(currentTempRow);
                        currentTempRow.iconIndices.clear();
                    }
                    tempCursorX = 0;
                }
                currentTempRow.iconIndices.push_back(i);
                iconToRowMap[i] = layoutRows.size();
                tempCursorX += itemSpan;
                if (tempCursorX >= iconsPerRow) {
                    layoutRows.push_back(currentTempRow);
                    currentTempRow.iconIndices.clear();
                    tempCursorX = 0;
                }
            }
            if (!currentTempRow.iconIndices.empty()) {
                currentTempRow.isFull = false;
                layoutRows.push_back(currentTempRow);
            }
        }

        int cursorX = 0;
        int cursorYBase = startY;

        auto getAdjustedStep = [&](void* targetWin) -> int {
            if (targetWin == nullptr) targetWin = containerWin;
            CoreContainerConfig targetData = getConfig(targetWin);
            float tSize = targetData.textSize;
            int currentStep = isList ? listStepY : (iconSize + gapY);
            if (tSize > 12.0f && tSize < 100.0f) {
                float scaleDiff = (tSize - 12.0f);
                if (isList) {
                    currentStep += static_cast<int>(std::round(scaleDiff * 3.0f));
                } else {
                    // 与 VinaIcon 的实际文本预览增高保持一致，防止下一行侵入组件。
                    currentStep += static_cast<int>(std::round(scaleDiff * 6.4f));
                }
            }
            return currentStep;
        };

        void* lastOrigin = nullptr;
        std::wstring currentSectionTitle = L"";

        float rowGapX = static_cast<float>(gapX);
        float rowCenteredOffsetX = static_cast<float>(centeredOffsetX);
        float rowAccumulatedX = static_cast<float>(actualStartX) + rowCenteredOffsetX;

        for (size_t i = 0; i < icons.size(); ++i) {
            icons[i].customWidth = -1.0f;
        }

        for (size_t i = 0; i < icons.size(); ++i) {
            if (!icons[i].isVisible) {
                icons[i].targetX = -100.0f;
                icons[i].targetY = -100.0f;
                icons[i].isCollapsed = true; // Treating hidden as collapsed
                continue;
            }

            void* targetWin = icons[i].originWindow ? icons[i].originWindow : containerWin;
            CoreContainerConfig targetData = getConfig(targetWin);

            if (targetWin != lastOrigin || (isSearchManager && mainData.IsInInlineFolderView && !icons[i].sectionTitle.empty())) {
                if (cursorX > 0) {
                    cursorX = 0;
                    cursorYBase += getAdjustedStep(lastOrigin ? lastOrigin : containerWin);
                }
                currentSectionTitle = icons[i].sectionTitle.empty() ? targetData.title : icons[i].sectionTitle;

                if (isIntegrated && !isInlineManager) {
                    CoreSectionHeaderLayoutData header;
                    header.originWindow = targetWin;
                    header.x1 = actualStartX + centeredOffsetX;
                    header.y1 = cursorYBase;
                    header.y2 = cursorYBase + 43;
                    header.x2 = containerWidth - (actualStartX + centeredOffsetX);
                    header.title = currentSectionTitle;
                    outHeaders.push_back(header);
                    cursorYBase += 58; // 43px height + 15px spacing
                }
                lastOrigin = targetWin;
            }

            if (icons[i].isCollapsed) {
                icons[i].targetX = -100.0f;
                icons[i].targetY = -100.0f;
                continue;
            }

            icons[i].isCollapsed = false;
            bool isTxt = (!isList && showTextPreviewFor(targetWin) && (MatchWildcard(L"*.txt", icons[i].path) || MatchWildcard(L"*.md", icons[i].path)));
            int itemSpan = getItemSpan(isTxt);

            if (mainData.IconSpacingType == 1 && !isList) {
                bool needsWrap = (i > 0 && iconToRowMap[i] != iconToRowMap[i - 1]);
                if (needsWrap && cursorX > 0) {
                    cursorX = 0;
                    void* prevWin = (i > 0 && icons[i - 1].originWindow) ? icons[i - 1].originWindow : containerWin;
                    cursorYBase += getAdjustedStep(prevWin);
                }
            } else {
                if (cursorX + itemSpan > iconsPerRow && cursorX > 0) {
                    cursorX = 0;
                    void* prevWin = (i > 0 && icons[i - 1].originWindow) ? icons[i - 1].originWindow : containerWin;
                    cursorYBase += getAdjustedStep(prevWin);
                }
            }

            if (cursorX == 0) {
                Row row;
                int numItems = 0;
                if (iconToRowMap[i] != static_cast<size_t>(-1)) {
                    row = layoutRows[iconToRowMap[i]];
                    numItems = static_cast<int>(row.iconIndices.size());
                }

                rowGapX = static_cast<float>(gapX);
                rowCenteredOffsetX = static_cast<float>(centeredOffsetX);

                if (mainData.IconSpacingType == 1 && !isList && numItems > 0) {
                    int totalSpan = 0;
                    for (size_t idx : row.iconIndices) {
                        void* tWin = icons[idx].originWindow ? icons[idx].originWindow : containerWin;
                        bool isTxt = (!isList && showTextPreviewFor(tWin) && (MatchWildcard(L"*.txt", icons[idx].path) || MatchWildcard(L"*.md", icons[idx].path)));
                        totalSpan += getItemSpan(isTxt);
                    }
                    bool isRowFull = row.isFull || (totalSpan >= iconsPerRow);

                    int totalUnshrunkWidth = 0;
                    int numWidgets = 0;
                    for (size_t idx : row.iconIndices) {
                        void* tWin = icons[idx].originWindow ? icons[idx].originWindow : containerWin;
                        bool isTxt = (!isList && showTextPreviewFor(tWin) && (MatchWildcard(L"*.txt", icons[idx].path) || MatchWildcard(L"*.md", icons[idx].path)));
                        if (isTxt) {
                            numWidgets++;
                            totalUnshrunkWidth += layoutTextWidgetWidth;
                        } else {
                            totalUnshrunkWidth += iconSize;
                        }
                    }

                    float spaceNeededWithStandard = static_cast<float>(totalUnshrunkWidth) + (numItems > 1 ? static_cast<float>(numItems - 1) * standardRowGapX : 0.0f) + 2.0f * standardRowCenteredOffsetX;
                    bool shouldShrinkAndFit = isRowFull || (spaceNeededWithStandard > static_cast<float>(availableWidth));

                    if (shouldShrinkAndFit) {
                        int totalItemsWidth = totalUnshrunkWidth;
                        float hoverExpandX = (spacingMode == 1) ? 8.0f : ((spacingMode == 2) ? 18.0f : 0.0f);
                        int E = 5 + static_cast<int>(hoverExpandX);

                        int stretchPerWidget = 0;
                        if (numWidgets > 0) {
                            int requiredSpace = availableWidth - numItems * (2 * E);
                            if (numItems > 1) {
                                float standardHoverGapX = standardRowGapX - 2.0f * E;
                                if (standardHoverGapX < 0.0f) standardHoverGapX = 0.0f;
                                requiredSpace -= static_cast<int>(std::round((numItems - 1) * standardHoverGapX));
                            }
                            int diff = requiredSpace - totalItemsWidth;
                            if (diff > 0) {
                                int maxTotalStretch = numWidgets * (layoutTextWidgetWidth / 4);
                                int actualTotalStretch = (diff < maxTotalStretch) ? diff : maxTotalStretch;
                                stretchPerWidget = actualTotalStretch / numWidgets;
                                totalItemsWidth += actualTotalStretch;
                            } else if (diff < 0) {
                                int maxTotalShrink = numWidgets * (layoutTextWidgetWidth / 6);
                                int actualTotalShrink = (-diff < maxTotalShrink) ? -diff : maxTotalShrink;
                                stretchPerWidget = -actualTotalShrink / numWidgets;
                                totalItemsWidth -= actualTotalShrink;
                            }
                        }

                        if (numItems > 1) {
                            int totalCollisionWidth = totalItemsWidth + numItems * (2 * E);
                            int remainingSpace = availableWidth - totalCollisionWidth;
                            if (remainingSpace >= 0) {
                                float hoverGapX = static_cast<float>(remainingSpace) / (numItems - 1);
                                float targetGapX = hoverGapX + 2.0f * E;
                                int maxGapX = gapX + (spacingMode == 0 ? 20 : (spacingMode == 1 ? 40 : 60));
                                if (targetGapX > static_cast<float>(maxGapX)) {
                                    rowGapX = static_cast<float>(maxGapX);
                                    float totalWidth = static_cast<float>(totalItemsWidth) + static_cast<float>(numItems - 1) * rowGapX;
                                    rowCenteredOffsetX = (static_cast<float>(availableWidth) - totalWidth) / 2.0f;
                                    if (rowCenteredOffsetX < 0.0f) rowCenteredOffsetX = 0.0f;
                                } else {
                                    rowGapX = targetGapX;
                                    rowCenteredOffsetX = static_cast<float>(E);
                                }
                            } else {
                                int spacingSpace = availableWidth - totalItemsWidth;
                                if (spacingSpace < 0) spacingSpace = 0;
                                float unit = static_cast<float>(spacingSpace) / (2.0f * numItems);
                                rowCenteredOffsetX = unit;
                                float totalGapsSpace = static_cast<float>(spacingSpace) - 2.0f * rowCenteredOffsetX;
                                if (totalGapsSpace < 0.0f) totalGapsSpace = 0.0f;
                                rowGapX = totalGapsSpace / (numItems - 1);
                            }
                        } else {
                            rowGapX = 0.0f;
                            rowCenteredOffsetX = static_cast<float>(availableWidth - totalItemsWidth) / 2.0f;
                            if (rowCenteredOffsetX < 0.0f) rowCenteredOffsetX = 0.0f;
                        }

                        for (size_t idx : row.iconIndices) {
                            void* tWin = icons[idx].originWindow ? icons[idx].originWindow : containerWin;
                            bool isTxt = (!isList && showTextPreviewFor(tWin) && (MatchWildcard(L"*.txt", icons[idx].path) || MatchWildcard(L"*.md", icons[idx].path)));
                            if (isTxt) icons[idx].customWidth = static_cast<float>(layoutTextWidgetWidth + stretchPerWidget);
                            else icons[idx].customWidth = -1.0f;
                        }
                    } else {
                        rowGapX = standardRowGapX;
                        rowCenteredOffsetX = standardRowCenteredOffsetX;
                        for (size_t idx : row.iconIndices) {
                            void* tWin = icons[idx].originWindow ? icons[idx].originWindow : containerWin;
                            bool isTxt = !isList && showTextPreviewFor(tWin) &&
                                (MatchWildcard(L"*.txt", icons[idx].path) || MatchWildcard(L"*.md", icons[idx].path));
                            icons[idx].customWidth = isTxt ? static_cast<float>(layoutTextWidgetWidth) : -1.0f;
                        }
                    }
                }
                rowAccumulatedX = static_cast<float>(actualStartX) + rowCenteredOffsetX;
            }

            if (isTxt && icons[i].customWidth <= 0.0f) {
                icons[i].customWidth = static_cast<float>(layoutTextWidgetWidth);
            }

            int newX;
            if (mainData.IconSpacingType == 1 && !isList) {
                newX = static_cast<int>(std::round(rowAccumulatedX));
                rowAccumulatedX += (icons[i].customWidth > 0.0f
                    ? icons[i].customWidth
                    : static_cast<float>(isTxt ? layoutTextWidgetWidth : iconSize)) + rowGapX;
            } else {
                if (customLayout && isTxt && iconsPerRow == 1) {
                    const int itemWidth = static_cast<int>(std::lround(icons[i].customWidth));
                    newX = actualStartX + (std::max)(0, availableWidth - itemWidth) / 2;
                } else {
                    newX = actualStartX + centeredOffsetX +
                        (isList ? 0 : cursorX * (iconSize + gapX));
                }
            }

            icons[i].targetX = static_cast<float>(newX);
            icons[i].targetY = static_cast<float>(cursorYBase);

            if (mainData.IconSpacingType == 1 && !isList) cursorX += itemSpan;
            else {
                cursorX += itemSpan;
                if (cursorX >= iconsPerRow) {
                    cursorX = 0;
                    cursorYBase += getAdjustedStep(targetWin);
                }
            }
        }

        if (isIntegrated && !isInlineManager && !isSearchManager && sectionInputs) {
			std::map<void*, CoreSectionHeaderLayoutData> headersByOrigin;
			int nextSectionY = startY;
			for (const auto& header : outHeaders) {
				headersByOrigin.emplace(header.originWindow, header);
				nextSectionY = (std::max)(nextSectionY, header.y2 + 15);
			}
			for (const auto& icon : icons) {
                if (!icon.isVisible || icon.isCollapsed || icon.targetY < -50.0f) continue;
                void* targetWin = icon.originWindow ? icon.originWindow : containerWin;
                nextSectionY = (std::max)(nextSectionY,
                    static_cast<int>(std::lround(icon.targetY)) +
					getAdjustedStep(targetWin));
			}

			std::vector<CoreSectionHeaderLayoutData> orderedHeaders;
			orderedHeaders.reserve(sectionInputs->size());
			std::map<void*, int> originOffsets;
			int emptySectionsBefore = 0;
			for (size_t sectionIndex = 0;
				sectionIndex < sectionInputs->size(); ++sectionIndex) {
				const auto& section = (*sectionInputs)[sectionIndex];
				if (!section.originWindow) continue;
				auto headerIt = headersByOrigin.find(section.originWindow);
				if (headerIt != headersByOrigin.end()) {
					CoreSectionHeaderLayoutData header = headerIt->second;
					const int offset = emptySectionsBefore * 58;
					header.y1 += offset;
					header.y2 += offset;
					orderedHeaders.push_back(header);
					originOffsets[section.originWindow] = offset;
					headersByOrigin.erase(headerIt);
					continue;
				}

				int insertionY = nextSectionY + emptySectionsBefore * 58;
				for (size_t followingIndex = sectionIndex + 1;
					followingIndex < sectionInputs->size(); ++followingIndex) {
					auto followingHeader = headersByOrigin.find(
						(*sectionInputs)[followingIndex].originWindow);
					if (followingHeader != headersByOrigin.end()) {
						insertionY = followingHeader->second.y1 + emptySectionsBefore * 58;
						break;
					}
				}
				CoreSectionHeaderLayoutData header;
				header.originWindow = section.originWindow;
				header.x1 = actualStartX + centeredOffsetX;
				header.y1 = insertionY;
				header.y2 = insertionY + 43;
				header.x2 = containerWidth - (actualStartX + centeredOffsetX);
				header.title = section.title.empty()
					? getConfig(section.originWindow).title : section.title;
				orderedHeaders.push_back(header);
				++emptySectionsBefore;
			}

			for (auto& icon : icons) {
				void* targetWin = icon.originWindow ? icon.originWindow : containerWin;
				auto offsetIt = originOffsets.find(targetWin);
				if (offsetIt != originOffsets.end() && icon.targetY > -50.0f) {
					icon.targetY += static_cast<float>(offsetIt->second);
				}
			}
			for (const auto& remaining : headersByOrigin) {
				CoreSectionHeaderLayoutData header = remaining.second;
				header.y1 += emptySectionsBefore * 58;
				header.y2 += emptySectionsBefore * 58;
				orderedHeaders.push_back(header);
			}
			outHeaders = std::move(orderedHeaders);
        }

        if (pagedLayout) {
            std::map<int, std::vector<size_t>> rows;
            for (size_t i = 0; i < icons.size(); ++i) {
                if (!icons[i].isVisible || icons[i].isCollapsed || icons[i].targetY < -50.0f) {
                    continue;
                }
                rows[static_cast<int>(std::lround(icons[i].targetY))].push_back(i);
            }

            int rowsPerPage = (std::max)(1, mainData.PagedLayoutMaxRows);
            const int indicatorReserve = horizontalPages ? 34 : 16;
            const int availablePageHeight = containerHeight > 0
                ? (std::max)(1, containerHeight - startY - indicatorReserve)
                : (std::numeric_limits<int>::max)() / 4;
            if (containerHeight > 0) {
                const int rowStep = (std::max)(1, getAdjustedStep(containerWin));
                const int rowsThatFit = (std::max)(1,
                    static_cast<int>(std::floor(
                        static_cast<double>(availablePageHeight + gapY) / rowStep)));
                rowsPerPage = (std::min)(rowsPerPage, rowsThatFit);
            }

            std::vector<float> pageOrigins;
            if (mainData.IsIntegrated) {
                std::map<void*, std::vector<int>> sectionRowYs;
                for (const auto& rowEntry : rows) {
                    if (rowEntry.second.empty()) continue;
                    const auto& firstIcon = icons[rowEntry.second.front()];
                    void* targetWin = firstIcon.originWindow
                        ? firstIcon.originWindow : containerWin;
                    sectionRowYs[targetWin].push_back(rowEntry.first);
                }

                const std::vector<CoreSectionHeaderLayoutData> originalHeaders = outHeaders;
                outHeaders.clear();
                int currentPage = -1;
                int currentPageRows = 0;
                bool pageHasContent = false;

                auto startPage = [&](float originY) {
                    ++currentPage;
                    pageOrigins.push_back(originY);
                    currentPageRows = 0;
                    pageHasContent = false;
                };
                auto assignRowToPage = [&](int rowY, int pageIndex) {
                    auto rowIt = rows.find(rowY);
                    if (rowIt == rows.end()) return;
                    for (size_t iconIndex : rowIt->second) {
                        icons[iconIndex].pageIndex = pageIndex;
                    }
                };
                auto rowEndY = [&](int rowY, void* originWindow) {
                    return rowY + (std::max)(1, getAdjustedStep(originWindow));
                };

                for (const auto& sourceHeader : originalHeaders) {
                    CoreSectionHeaderLayoutData header = sourceHeader;
                    auto groupIt = sectionRowYs.find(header.originWindow);
                    const std::vector<int> emptyRows;
                    const auto& groupRows = groupIt != sectionRowYs.end()
                        ? groupIt->second : emptyRows;
                    const int groupEndY = groupRows.empty()
                        ? header.y2 + 15
                        : rowEndY(groupRows.back(), header.originWindow);
                    const int groupRowCount = static_cast<int>(groupRows.size());
                    const bool groupFitsFreshPage =
                        groupRowCount <= rowsPerPage &&
                        groupEndY - header.y1 <= availablePageHeight;

                    if (currentPage < 0) startPage(static_cast<float>(header.y1));
                    const bool groupFitsCurrentPage =
                        currentPageRows + groupRowCount <= rowsPerPage &&
                        groupEndY - pageOrigins[static_cast<size_t>(currentPage)] <=
                            availablePageHeight;
                    if (pageHasContent &&
                        (!groupFitsCurrentPage || !groupFitsFreshPage)) {
                        startPage(static_cast<float>(header.y1));
                    }

                    header.pageIndex = currentPage;
                    outHeaders.push_back(header);
                    pageHasContent = true;

                    int rowsInCurrentChunk = 0;
                    int continuationIndex = 0;
                    for (int rowY : groupRows) {
                        const int endY = rowEndY(rowY, header.originWindow);
                        const bool exceedsRowLimit = currentPageRows >= rowsPerPage;
                        const bool exceedsHeight =
                            endY - pageOrigins[static_cast<size_t>(currentPage)] >
                                availablePageHeight;
                        if (rowsInCurrentChunk > 0 &&
                            (exceedsRowLimit || exceedsHeight)) {
                            CoreSectionHeaderLayoutData continuation = sourceHeader;
                            continuation.isContinuation = true;
                            continuation.continuationIndex = ++continuationIndex;
                            continuation.y1 = rowY - 58;
                            continuation.y2 = continuation.y1 + 43;
                            startPage(static_cast<float>(continuation.y1));
                            continuation.pageIndex = currentPage;
                            outHeaders.push_back(continuation);
                            pageHasContent = true;
                            rowsInCurrentChunk = 0;
                        }
                        assignRowToPage(rowY, currentPage);
                        ++currentPageRows;
                        ++rowsInCurrentChunk;
                    }
                }

				std::map<void*, int> sectionPageCounts;
				for (const auto& header : outHeaders) {
					sectionPageCounts[header.originWindow] = (std::max)(
						sectionPageCounts[header.originWindow],
						header.continuationIndex + 1);
				}
				for (auto& header : outHeaders) {
					header.sectionPageIndex = header.continuationIndex + 1;
					header.sectionPageCount = (std::max)(1,
						sectionPageCounts[header.originWindow]);
				}

                if (pageOrigins.empty()) {
                    pageOrigins.push_back(static_cast<float>(startY));
                }
            }
            else {
                const int rowCount = static_cast<int>(rows.size());
                const int pageCount = (std::max)(1,
                    (rowCount + rowsPerPage - 1) / rowsPerPage);
                pageOrigins.assign(static_cast<size_t>(pageCount),
                    static_cast<float>(startY));

                int rowOrdinal = 0;
                for (const auto& rowEntry : rows) {
                    const int pageIndex = rowOrdinal / rowsPerPage;
                    if (rowOrdinal % rowsPerPage == 0) {
                        pageOrigins[static_cast<size_t>(pageIndex)] =
                            static_cast<float>(rowEntry.first);
                    }
                    for (size_t iconIndex : rowEntry.second) {
                        icons[iconIndex].pageIndex = pageIndex;
                    }
                    ++rowOrdinal;
                }

                for (auto& header : outHeaders) {
                    int headerPage = 0;
                    bool foundFollowingIcon = false;
                    float nearestY = (std::numeric_limits<float>::max)();
                    for (const auto& icon : icons) {
                        if (!icon.isVisible || icon.isCollapsed ||
                            icon.originWindow != header.originWindow ||
                            icon.targetY < static_cast<float>(header.y1)) {
                            continue;
                        }
                        if (icon.targetY < nearestY) {
                            nearestY = icon.targetY;
                            headerPage = icon.pageIndex;
                            foundFollowingIcon = true;
                        }
                    }
                    if (!foundFollowingIcon && !rows.empty()) {
                        auto rowIt = rows.lower_bound(header.y1);
                        if (rowIt == rows.end()) rowIt = std::prev(rows.end());
                        if (!rowIt->second.empty()) {
                            headerPage = icons[rowIt->second.front()].pageIndex;
                        }
                    }
                    header.pageIndex = std::clamp(headerPage, 0, pageCount - 1);
                    pageOrigins[static_cast<size_t>(header.pageIndex)] = (std::min)(
                        pageOrigins[static_cast<size_t>(header.pageIndex)],
                        static_cast<float>(header.y1));
                }
            }

            const int pageCount = (std::max)(1,
                static_cast<int>(pageOrigins.size()));
            int currentPage = std::clamp(
                mainData.PagedLayoutCurrentPage, 0, pageCount - 1);
            if (layoutAnchor && layoutAnchor->originWindow) {
                int resolvedPage = -1;
                auto resolveExactIcon = [&]() {
                    if (!layoutAnchor->iconToken && layoutAnchor->iconId < 0) return;
                    for (const auto& icon : icons) {
                        if (!icon.isVisible || icon.isCollapsed ||
                            icon.originWindow != layoutAnchor->originWindow) continue;
                        const bool identityMatches = layoutAnchor->iconToken
                            ? icon.stableToken == layoutAnchor->iconToken
                            : icon.stableId == layoutAnchor->iconId;
                        if (!identityMatches) continue;
                        resolvedPage = icon.pageIndex;
                        return;
                    }
                };
                auto resolveGroupIcon = [&]() {
                    if (resolvedPage >= 0) return;
                    for (const auto& icon : icons) {
                        if (!icon.isVisible || icon.isCollapsed ||
                            icon.originWindow != layoutAnchor->originWindow) continue;
                        resolvedPage = icon.pageIndex;
                        return;
                    }
                };
                auto resolveExactHeader = [&]() {
                    if (resolvedPage >= 0) return;
                    for (const auto& header : outHeaders) {
                        if (header.originWindow == layoutAnchor->originWindow &&
                            (layoutAnchor->sectionTitle.empty() ||
                                header.title == layoutAnchor->sectionTitle) &&
                            header.continuationIndex ==
                                layoutAnchor->continuationIndex) {
                            resolvedPage = header.pageIndex;
                            return;
                        }
                    }
                };
                auto resolveGroupHeader = [&]() {
                    if (resolvedPage >= 0) return;
                    const CoreSectionHeaderLayoutData* bestHeader = nullptr;
                    int bestDistance = (std::numeric_limits<int>::max)();
                    for (const auto& header : outHeaders) {
                        if (header.originWindow != layoutAnchor->originWindow) continue;
                        const int distance = std::abs(
                            header.continuationIndex -
                            layoutAnchor->continuationIndex);
                        if (!bestHeader || distance < bestDistance) {
                            bestHeader = &header;
                            bestDistance = distance;
                        }
                    }
                    if (bestHeader) resolvedPage = bestHeader->pageIndex;
                };

                if (layoutAnchor->preferHeader) {
                    resolveExactHeader();
                    resolveGroupHeader();
                    resolveExactIcon();
                    resolveGroupIcon();
                }
                else {
                    resolveExactIcon();
                    resolveExactHeader();
                    resolveGroupHeader();
                    resolveGroupIcon();
                }
                if (resolvedPage >= 0) {
                    currentPage = std::clamp(resolvedPage, 0, pageCount - 1);
                    layoutResult.anchorResolved = true;
                    layoutResult.resolvedAnchorPage = currentPage;
                }
            }

            for (auto& icon : icons) {
                if (!icon.isVisible || icon.isCollapsed || icon.targetY < -50.0f) continue;
                const int pageIndex = std::clamp(icon.pageIndex, 0, pageCount - 1);
                const int pageDelta = pageIndex - currentPage;
                if (horizontalPages) {
                    icon.targetX += static_cast<float>(pageDelta) *
                        static_cast<float>(containerWidth);
                    icon.targetY = static_cast<float>(startY) + icon.targetY -
                        pageOrigins[static_cast<size_t>(pageIndex)];
                } else {
                    icon.targetY = static_cast<float>(startY) + icon.targetY -
                        pageOrigins[static_cast<size_t>(pageIndex)] +
                        static_cast<float>(pageDelta) *
                        static_cast<float>((std::max)(1, containerHeight));
                }
            }
            for (auto& header : outHeaders) {
                const int pageIndex = std::clamp(header.pageIndex, 0, pageCount - 1);
                const int pageDelta = pageIndex - currentPage;
                const float originY = pageOrigins[static_cast<size_t>(pageIndex)];
                if (horizontalPages) {
                    const int pageShift = pageDelta * containerWidth;
                    header.x1 += pageShift;
                    header.x2 += pageShift;
                    header.y1 = static_cast<int>(std::lround(
                        static_cast<float>(startY) + header.y1 - originY));
                    header.y2 = static_cast<int>(std::lround(
                        static_cast<float>(startY) + header.y2 - originY));
                } else {
                    const int pageShift = pageDelta * (std::max)(1, containerHeight);
                    header.y1 = static_cast<int>(std::lround(
                        static_cast<float>(startY) + header.y1 - originY)) + pageShift;
                    header.y2 = static_cast<int>(std::lround(
                        static_cast<float>(startY) + header.y2 - originY)) + pageShift;
                }
            }

            layoutResult.isPaged = true;
            layoutResult.pageCount = pageCount;
            layoutResult.currentPage = currentPage;
            layoutResult.columnsPerPage = iconsPerRow;
            layoutResult.rowsPerPage = rowsPerPage;
        } else {
            for (auto& icon : icons) icon.pageIndex = 0;
            for (auto& header : outHeaders) header.pageIndex = 0;
            layoutResult.columnsPerPage = iconsPerRow;
        }

        if (outResult) *outResult = layoutResult;

        // Split interaction cells at adjacent item/row midpoints. Keep 4px
        // horizontally and 2px vertically without changing rendered positions.
        struct InteractionRow {
            float top = 0.0f;
            float bottom = 0.0f;
            std::vector<size_t> iconIndices;
        };

        std::map<int, InteractionRow> interactionRows;
        auto getItemWidth = [&](size_t index) {
            if (icons[index].customWidth > 0.0f) return icons[index].customWidth;
            void* targetWin = icons[index].originWindow ? icons[index].originWindow : containerWin;
            bool isTxt = !isList && showTextPreviewFor(targetWin) &&
                (MatchWildcard(L"*.txt", icons[index].path) || MatchWildcard(L"*.md", icons[index].path));
            return static_cast<float>(isTxt ? textWidgetWidth : iconSize);
        };
        auto getItemHeight = [&](size_t index) {
            if (isList) return iconSize / 2.0f + 6.0f;
            void* targetWin = icons[index].originWindow ? icons[index].originWindow : containerWin;
            const bool isTxt = showTextPreviewFor(targetWin) &&
                (MatchWildcard(L"*.txt", icons[index].path) || MatchWildcard(L"*.md", icons[index].path));
            float height = static_cast<float>(iconSize);
            const float textSize = getConfig(targetWin).textSize;
            if (isTxt && textSize > 12.0f && textSize < 100.0f) {
                height += (textSize - 12.0f) * 6.4f;
            }
            return height;
        };

        for (size_t i = 0; i < icons.size(); ++i) {
            auto& icon = icons[i];
            icon.hasInteractionClip = false;
            if (!icon.isVisible || icon.isCollapsed || icon.targetX < -50.0f || icon.targetY < -50.0f) continue;

            int rowKey = static_cast<int>(std::lround(icon.targetY));
            auto& row = interactionRows[rowKey];
            const float itemHeight = getItemHeight(i);
            if (row.iconIndices.empty()) {
                row.top = icon.targetY;
                row.bottom = icon.targetY + itemHeight;
            } else {
                row.top = (std::min)(row.top, icon.targetY);
                row.bottom = (std::max)(row.bottom, icon.targetY + itemHeight);
            }
            row.iconIndices.push_back(i);

            icon.interactionClipLeftOffset = 1.0f - icon.targetX;
            icon.interactionClipTopOffset = -100000.0f;
            icon.interactionClipRightOffset =
                (std::max)(1.0f, static_cast<float>(containerWidth) - 1.0f) - icon.targetX;
            icon.interactionClipBottomOffset = 100000.0f;
            icon.hasInteractionClip = true;
        }

        for (auto& rowEntry : interactionRows) {
            auto& row = rowEntry.second;
            std::sort(row.iconIndices.begin(), row.iconIndices.end(), [&](size_t lhs, size_t rhs) {
                return icons[lhs].targetX < icons[rhs].targetX;
            });

            for (size_t pos = 1; pos < row.iconIndices.size(); ++pos) {
                size_t leftIndex = row.iconIndices[pos - 1];
                size_t rightIndex = row.iconIndices[pos];
                float leftItemRight = icons[leftIndex].targetX + getItemWidth(leftIndex);
                float rightItemLeft = icons[rightIndex].targetX;
                float separator = (leftItemRight + rightItemLeft) * 0.5f;

                if (leftItemRight > rightItemLeft) {
                    float leftCenter = icons[leftIndex].targetX + getItemWidth(leftIndex) * 0.5f;
                    float rightCenter = icons[rightIndex].targetX + getItemWidth(rightIndex) * 0.5f;
                    separator = (leftCenter + rightCenter) * 0.5f;
                }

                icons[leftIndex].interactionClipRightOffset =
                    (std::min)(icons[leftIndex].interactionClipRightOffset,
                        separator - 2.0f - icons[leftIndex].targetX);
                icons[rightIndex].interactionClipLeftOffset =
                    (std::max)(icons[rightIndex].interactionClipLeftOffset,
                        separator + 2.0f - icons[rightIndex].targetX);
            }
        }

        std::vector<InteractionRow*> orderedRows;
        orderedRows.reserve(interactionRows.size());
        for (auto& rowEntry : interactionRows) orderedRows.push_back(&rowEntry.second);
        std::sort(orderedRows.begin(), orderedRows.end(), [](const InteractionRow* lhs, const InteractionRow* rhs) {
            return lhs->top < rhs->top;
        });

        for (size_t rowIndex = 1; rowIndex < orderedRows.size(); ++rowIndex) {
            InteractionRow& upper = *orderedRows[rowIndex - 1];
            InteractionRow& lower = *orderedRows[rowIndex];
            float separator = (upper.bottom + lower.top) * 0.5f;
            for (size_t index : upper.iconIndices) {
                icons[index].interactionClipBottomOffset =
                    (std::min)(icons[index].interactionClipBottomOffset,
                        separator + 1.0f - icons[index].targetY);
            }
            for (size_t index : lower.iconIndices) {
                icons[index].interactionClipTopOffset =
                    (std::max)(icons[index].interactionClipTopOffset,
                        separator + 1.0f - icons[index].targetY);
            }
        }
    }
}
