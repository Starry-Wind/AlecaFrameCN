// Runs only in AlecaFrame's relic overlay. The official callback remains the
// source of truth for every language it already supports.
function GetRelicWindowDataZH(testMode, callback) {
    const native = plugin.get();
    native.GetRelicWindowData(testMode, (success, data, timeLeftSeconds) => {
        if (success || !String(data).includes("Unsupported Warframe language")) {
            callback(success, data, timeLeftSeconds);
            return;
        }
        console.log("[AlecaFrame 中文遗物] 官方识别返回不支持语言，开始读取本机结果");

        const finish = (rewards) => {
            const payload = {
                relicRewards: rewards,
                globalData: { platinum: -1, ducats: -1 }
            };
            console.log("[AlecaFrame 中文遗物] 已识别 " + rewards.length + " 件奖励");
            callback(true, JSON.stringify(payload), Math.max(8, Number(timeLeftSeconds) || 0));
        };

        readLocalResult(startTime - 5000)
            .then(response => {
                if (!response.ok || !Array.isArray(response.rewards) || !response.rewards.length) {
                    throw new Error(response.error || "中文遗物截图识别失败");
                }
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
                finish(rewards);
                const refresh = () => {
                    if (window.relicsApp) relicsApp.relics = rewards.slice();
                };
                fillPrices(native, response.rewards, rewards, refresh);
                fillFoundry(native, response.rewards, rewards, refresh);
            })
            .catch(error => {
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
