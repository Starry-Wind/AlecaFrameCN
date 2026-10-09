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
            .then(async response => {
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
                fillPrices(native, response.rewards, rewards).then(refresh);
                fillInventory(native, rewards).then(refresh);
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

function fillPrices(native, source, rewards) {
    return new Promise(resolve => {
        const timer = setTimeout(resolve, 3000);
        try {
            const names = source.map(item => item.englishName);
            native.getHugePriceList(JSON.stringify(names), (success, data) => {
                clearTimeout(timer);
                try {
                    if (success) {
                        const prices = JSON.parse(data);
                        rewards.forEach((reward, i) => {
                            const part = prices[i];
                            if (part && Number(part.post) > 0) reward.platinum = Number(part.post);
                        });
                    }
                } catch (error) { console.warn("[AlecaFrame 中文遗物] 价格读取失败", error); }
                resolve();
            });
        } catch (error) { clearTimeout(timer); resolve(); }
    });
}

function fillInventory(native, rewards) {
    return new Promise(resolve => {
        const timer = setTimeout(resolve, 3000);
        const filter = {
            type: "allParts", search: "", order: "name",
            orderLargerToSmaller: false, yesnoFilters: "{}", onlyOwned: false
        };
        try {
            native.getFilteredInventory(JSON.stringify(filter), true, (success, data) => {
                clearTimeout(timer);
                try {
                    if (success) {
                        const items = JSON.parse(data);
                        const byPart = new Map();
                        items.forEach(item => {
                            (item.components || []).forEach(component => {
                                const key = component.baseData && component.baseData.uniqueName;
                                if (key) byPart.set(key, { item, component });
                            });
                        });
                        rewards.forEach(reward => {
                            const match = byPart.get(reward.internalName);
                            if (!match) return;
                            reward.countOwned = Number(match.component.baseData.quantityOwned) || 0;
                            reward.isFav = !!(match.component.baseData.isFav ||
                                match.component.isFav || match.item.isFav);
                            reward.isPartOfOwned = !!match.item.owned;
                        });
                    }
                } catch (error) { console.warn("[AlecaFrame 中文遗物] 库存读取失败", error); }
                resolve();
            });
        } catch (error) { clearTimeout(timer); resolve(); }
    });
}
