// Runs only in AlecaFrame's relic overlay. The official callback remains the
// source of truth for every language it already supports.
function GetRelicWindowDataZH(testMode, callback) {
    const requestId = (GetRelicWindowDataZH.requestId || 0) + 1;
    GetRelicWindowDataZH.requestId = requestId;
    const native = plugin.get();
    native.GetRelicWindowData(testMode, (success, data, timeLeftSeconds) => {
        if (GetRelicWindowDataZH.requestId !== requestId) return;
        if (success || !String(data).includes("Unsupported Warframe language")) {
            callback(success, data, timeLeftSeconds);
            return;
        }
        console.log("[AlecaFrame 中文遗物] 官方识别返回不支持语言，开始读取本机结果");

        const verifyDisplay = expectedCount => {
            if (typeof document === "undefined" || !window.relicsApp) return;
            const verify = () => {
                if (GetRelicWindowDataZH.requestId !== requestId) return;
                const modelCount = relicsApp.relics.length;
                const renderedCount = document.querySelectorAll(".relicHolder > .relic").length;
                const message = "[AlecaFrame 中文遗物] 卡片校验：画面=" + expectedCount +
                    "，数据=" + modelCount + "，界面=" + renderedCount;
                if (modelCount !== expectedCount || renderedCount !== expectedCount) console.error(message);
                else console.log(message);
            };
            if (typeof relicsApp.$nextTick === "function") relicsApp.$nextTick(verify);
            else Promise.resolve().then(verify);
        };
        const finish = (rewards, expectedCount) => {
            const payload = {
                relicRewards: rewards,
                globalData: { platinum: -1, ducats: -1 }
            };
            console.log("[AlecaFrame 中文遗物] 已识别 " + rewards.length + " 件奖励");
            callback(true, JSON.stringify(payload), Math.max(8, Number(timeLeftSeconds) || 0));
            verifyDisplay(expectedCount);
        };

        readLocalResult(startTime - 5000)
            .then(response => {
                if (GetRelicWindowDataZH.requestId !== requestId) return;
                if (!response.ok || !Array.isArray(response.rewards) || !response.rewards.length) {
                    throw new Error(response.error || "中文遗物截图识别失败");
                }
                const expectedCount = Number(response.expectedCount) || response.rewards.length;
                if (expectedCount !== response.rewards.length) throw new Error("奖励识别不完整：画面中有 " +
                    expectedCount + " 件奖励，但仅收到 " + response.rewards.length + " 件");
                const rewards = response.rewards.map(item => ({
                    name: item.name,
                    englishName: item.englishName,
                    internalName: item.internalName,
                    detected: true,
                    errorProb: 0,
                    platinum: -1,
                    ducats: item.ducats,
                    setPlat: -1,
                    countOwned: 0,
                    totalToOwn: item.totalToOwn,
                    isPartOfOwned: false,
                    isFav: false,
                    isItemVaulted: item.isItemVaulted,
                    componentData: []
                }));
                finish(rewards, expectedCount);
                const refresh = () => {
                    if (GetRelicWindowDataZH.requestId !== requestId) return;
                    if (window.relicsApp) relicsApp.relics = rewards.slice();
                    verifyDisplay(expectedCount);
                };
                fillPrices(native, response.rewards, rewards, refresh);
                fillFoundry(native, response.rewards, rewards, refresh);
            })
            .catch(error => {
                if (GetRelicWindowDataZH.requestId !== requestId) return;
                console.error("[AlecaFrame 中文遗物] " + error);
                callback(false, String(error.message || error), timeLeftSeconds);
            });
    });
}

function readLocalResult(minimumSourceTime) {
    const path = overwolf.io.paths.localAppData + "/AlecaFrame-ZH-Patch/relic-result.json";
    const encoding = overwolf.io.enums.eEncoding.UTF8;
    const started = Date.now();
    let firstRead = true;
    return new Promise((resolve, reject) => {
        function poll() {
            if (Date.now() - started > 14000) {
                reject(new Error("中文遗物识别超时"));
                return;
            }
            overwolf.io.readFileContents(path, encoding, result => {
                if (firstRead) {
                    firstRead = false;
                    console.log("[AlecaFrame 中文遗物] 本地结果读取：" +
                        (result.success ? "成功" : String(result.error || "文件尚未就绪")));
                }
                if (result.success) {
                    try {
                        const data = JSON.parse(result.content);
                        if (Number(data.sourceMtime) >= minimumSourceTime) {
                            resolve(data);
                            return;
                        }
                    } catch (error) { console.warn("[AlecaFrame 中文遗物] 结果文件读取失败", error); }
                }
                setTimeout(poll, 250);
            });
        }
        poll();
    });
}

function fillPrices(native, source, rewards, refresh) {
    try {
        // Older market entries use " Set"; some newer entries use the parent
        // name directly. Keep both queries in English, independent of display names.
        const parents = source.map(item =>
            /\bPrime\b/.test(item.parentName || "") ? item.parentName : "");
        const names = Array.from(new Set(source.map(item => item.englishName)
            .concat(parents.filter(Boolean).flatMap(name => [name + " Set", name]))));
        native.getHugePriceList(JSON.stringify(names), (success, data) => {
            try {
                if (!success) return;
                const prices = JSON.parse(data);
                const byName = new Map(names.map((name, i) => [name, prices[i]]));
                const priceFor = name => {
                    const item = byName.get(name);
                    return item && Number(item.post) > 0 ? Number(item.post) : -1;
                };
                rewards.forEach((reward, i) => {
                    reward.platinum = priceFor(source[i].englishName);
                    if (parents[i]) {
                        const setPrice = priceFor(parents[i] + " Set");
                        reward.setPlat = setPrice > 0 ? setPrice : priceFor(parents[i]);
                    }
                });
                // A native price request can finish after the old three-second
                // timeout. Refresh on the actual callback so Vue sees late data.
                refresh();
            } catch (error) { console.warn("[AlecaFrame 中文遗物] 价格读取失败", error); }
        });
    } catch (error) { console.warn("[AlecaFrame 中文遗物] 价格读取失败", error); }
}

function fillFoundry(native, source, rewards, refresh) {
    const filter = {
        type: "all", search: "", order: "name",
        orderLargerToSmaller: false, yesnoFilters: "{}"
    };
    try {
        // Inventory entries are flat. Foundry entries contain the native recipe,
        // component pictures, quantities, favourites and ownership/mastery flags.
        native.getFilteredFoundry(JSON.stringify(filter), true, (success, data) => {
            try {
                if (!success) return;
                const items = JSON.parse(data);
                const byPart = new Map();
                items.forEach(item => {
                    (item.components || []).forEach(component => {
                        const key = component.uniqueName;
                        if (!key) return;
                        if (!byPart.has(key)) byPart.set(key, []);
                        byPart.get(key).push({ item, component });
                    });
                });
                rewards.forEach((reward, i) => {
                    const matches = byPart.get(reward.internalName);
                    if (!matches) return;
                    // A component can be used by several recipes. Use the parent
                    // selected by recognition instead of the last indexed recipe.
                    const parent = source[i];
                    const match = matches.find(entry => entry.item.internalName === parent.parentInternalName) ||
                        matches.find(entry => entry.item.name === parent.parentName) || matches[0];
                    const item = match.item;
                    const component = match.component;
                    reward.countOwned = Number(component.quantityOwned) || 0;
                    reward.totalToOwn = Number(component.neccessaryAmount) || reward.totalToOwn;
                    reward.isFav = !!(component.isFav || component.isFavOnlyPart || item.isFav);
                    reward.isPartOfOwned = !!(item.owned || item.mastered || item.pendingInFoundry || component.parentOwned);
                    // Match the official overlay: only Prime recipes, without
                    // resource/research ingredients, and highlight this reward.
                    if (/\bPrime\b/.test(reward.englishName + " " + (parent.parentName || item.name))) {
                        reward.componentData = item.components
                            .filter(part => part.uniqueName &&
                                !part.uniqueName.includes("/MiscItems/") &&
                                !part.uniqueName.includes("/Research/"))
                            .map(part => Object.assign({}, part, {
                                recipeHighlightedComponent: part.uniqueName === reward.internalName
                            }));
                    }
                });
                refresh();
                console.log("[AlecaFrame 中文遗物] 已补全奖励的铸造部件及持有数量");
            } catch (error) { console.warn("[AlecaFrame 中文遗物] 铸造数据读取失败", error); }
        });
    } catch (error) { console.warn("[AlecaFrame 中文遗物] 铸造数据读取失败", error); }
}

// Keep the official DPI/zoom calculation, then fit the entire panel into the
// game viewport. Its fixed minimum height otherwise cuts off the 720p footer.
(function fitRelicOverlayToViewport() {
    const official = window.calculateContentPixelBounds;
    if (typeof official !== "function") return;
    window.calculateContentPixelBounds = function (screenWidth, screenHeight, selectedZoom) {
        const bounds = official(screenWidth, screenHeight, selectedZoom);
        if (!(screenWidth > 0 && screenHeight > 0)) return bounds;
        const margin = Math.max(4, Math.round(Math.min(screenWidth, screenHeight) * 0.01));
        bounds.width = Math.round(Math.min(bounds.width, screenWidth - margin * 2));
        bounds.height = Math.round(Math.min(bounds.height, screenHeight * 0.38, screenHeight - margin * 2));
        bounds.left = Math.round(Math.max(margin, Math.min(bounds.left, screenWidth - margin - bounds.width)));
        bounds.top = Math.round(Math.max(margin, Math.min(bounds.top, screenHeight - margin - bounds.height)));
        return bounds;
    };
})();

(function fitRelicOwnershipLabels() {
    if (typeof document === "undefined") return;
    const style = document.createElement("style");
    style.textContent = `
        @media (max-height: 320px), (max-width: 1200px) {
            .relicPart .relicItemOwnershipInfo { width: 90%; }
            .relicPart .relicItemOwnershipInfo > :first-child {
                flex: 1 1 auto; min-width: 0; width: auto;
                font-size: min(16px, 1.1rem); padding: 2px 6px;
            }
            .relicPart .relicItemOwnershipInfo > .relicItemUnlockedCount {
                flex: 0 0 auto; min-width: max-content; white-space: nowrap;
                font-size: min(16px, 1.1rem); padding: 2px 5px;
            }
        }`;
    document.head.appendChild(style);
})();
