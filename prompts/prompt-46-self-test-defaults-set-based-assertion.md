# prompt-46：让「自检 defaults 域」那条断言真正能抓住原始缺陷

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定、编号与两端口径对齐见 `prompts/README.md`。

> **来源**：**批次 D 独立验收发现的 P2-1**（见 `prompts/batch-D-verification-report.md` §5 与 §9）。prompt-45 修好了功能，但它新加的那条断言**经变异测试证明几乎不设防** —— 本份只修断言，不改功能。
> **依赖**：prompt-31 的测试底座、prompt-45 的固定域名实现。

## 1. Goal

1. 把 `self test defaults suite file count does not grow` 的判据从**计数型**（`count <= before`）改成**名字/集合型**，并让它在检查前**强制落盘**。
2. 改造后必须满足一条硬指标：**把 `suiteName` 改回「每次随机 UUID」这个原始缺陷，该断言必须失败。**（现状：改回 UUID 它也照样通过。）

## 2. Evidence

**实测环境**：2026-09-29 批次 D 验收，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD `83e0ba7`，Apple Swift 6.4 / Xcode 27.0。以下均为实测值。

### 2.1 现状（已核实）

- `Sources/ClipShelfLite/Support/SelfTest.swift:14` —— 固定域名 `let suiteName = "ClipShelf.SelfTest.Active"`
- `:15-27` —— `preferencesURL` / `suitePlistURL` / `selfTestDefaultsFileCount()` 闭包 / `defaultsFileCountBeforeRun`
- `:28-33` —— `clearSelfTestDefaults` 闭包（`removePersistentDomain` + `synchronize` + `removeItem`）
- `:669-677` —— 待改造的断言：
  ```swift
  clearSelfTestDefaults()
  results.append(check(
      name: "self test defaults suite file count does not grow",
      condition: selfTestDefaultsFileCount() <= defaultsFileCountBeforeRun,
      ...
  ```

### 2.2 为什么它守不住（变异测试实测）

**变异 M45b**：把 `clearSelfTestDefaults` **整段变成空操作**（= 完全放弃清理）。
→ 结果 **81 PASS / 1 FAIL**，唯一 FAIL 是**既有的** `history limit default`；**目标断言照样通过**。

两个原因，都实测过：

1. **cfprefsd 异步回写** —— 断言执行时那个 plist 常常**还没落盘**，于是 `count == 0`，`0 <= 0` 恒真。
   **铁证**：验收时跑完清理统计为 `0`，**1 分钟后（15:20）同一个文件又出现**（42 字节、内容 `{}`）。
2. **`<=` 自我掩蔽** —— 一旦已有残留，`before == after`，条件自动成立。

**真正抓到问题的是既有断言** `history limit default`（`SelfTest.swift:157-162`）：清理关掉后第 1 次运行写入 `history.maxItems = 5`，第 2 次运行读到 `5` 而非默认 `100` → FAIL。（只出现 1 条 FAIL 而不是 2 条，说明是「第 2 次运行才被污染」；套件在同一进程里跑两遍，`82 = 2 × 41`。）

### 2.3 一个必须尊重的实测事实

**即使清理正常工作，那个 plist 也会在进程退出后以 42 字节 `{}` 的形式回来。**
→ 所以「断言时该文件不存在」**是不现实的判据**（prompt-45 已经正确地拒绝了它）。**新判据不能要求文件不存在。**

## 3. Scope

**受影响**
- `Sources/ClipShelfLite/Support/SelfTest.swift` —— **只动** suite 生命周期区（`:14-38`）与该条断言（`:669-677`）

**不得改变的部分**
- **固定域名 `ClipShelf.SelfTest.Active` 保持不变**（**不要**改回 UUID —— 那是本份要防的缺陷）。
- `CLIPSHELF_DEFAULTS_SUITE` 的语义不变：有该环境变量时仍必须返回**隔离的** `UserDefaults(suiteName:)`，**不得回退 `.standard`**。
- **`--self-test` 的报告格式不变**：`tests/parity/run.sh` 的 T11 会解析它（断言名 + `success` 文案）。
- **其余 40 条断言一条都不许改、不许删。**
- **`tests/parity/` 归 WorkBuddy 所有，不得修改。**
- `~/Library/Preferences/` 下**其它文件一个都不能动**（`ClipShelf.plist` / `local.codex.*` / `ClipShelf.Runtime43.*`）；**禁止任何"清空 Preferences 目录"式的实现**。
- 不改任何产品行为（本份是**测试侧**改造）。

## 4. Constraints

1. **判据必须是名字/集合型，不得是计数型。** 计数型已被证明会被「异步落盘 + `<=`」双重掩蔽。
2. **检查前必须强制落盘**：调用 `CFPreferencesAppSynchronize(suiteName as CFString)`（或等价的强制同步手段），把 cfprefsd 的待写内容逼到磁盘，否则任何文件系统判据都可能看到陈旧状态。
3. **判据不能要求文件不存在**（见 §2.3）。推荐形态（**不强制**，你判断）：
   > 「`~/Library/Preferences/` 里匹配 `ClipShelf.SelfTest.*.plist` 的文件中，**不属于固定域名**的那些必须为空。」
   —— 这样固定域名那个文件在不在都合法，而**一旦出现别的名字（例如 UUID）就失败**，正好对上原始缺陷。
4. **不得为了让断言稳定而放宽它**（例如退回 `<=` 计数、或写成永真）。若某个形态实测不稳定，**说明原因并给出你实测的稳定性（连续 5 次）**。
5. 断言**总数保持 41**（这是改造，不是新增）。若你判断必须再加一条，最多 42，并在报告里说明理由。
6. **本份不迁移测试框架**（`XCTest` / Swift Testing 的迁移是独立决策，见 `prompts/README.md`「环境变更（2026-09-29）」）。
7. 不引入第三方依赖。

## 5. Acceptance

- **A** 未变异时 `ClipShelf --self-test <报告>` **41/41、退出码 0**（连跑 3 次，贴原始输出）。
- **B（本份的核心）变异对照**：把 `SelfTest.swift:14` 的 `suiteName` 改回 `"ClipShelf.SelfTest.\(UUID().uuidString)"`（**这正是 prompt-45 修掉的那个原始缺陷**），改造后的断言**必须 FAIL**。
  - 贴出变异前后的原始输出。
  - **对照说明**：改造**之前**，同一条断言在这种情况下是**通过**的（本报告 §2.2 已实测）。
  - 验完请把变异还原，并在报告里给出还原后的全绿输出。
- **C** `swift build --disable-sandbox` 通过；`swift test --disable-sandbox` 通过（**注意**：本仓库在 `~/Documents` 下受文件提供器管理，`.xctest` 可能因 `com.apple.FinderInfo` 导致 `codesign` 失败 → 先 `rm -rf .build` 重建，或改用 `swift test --disable-sandbox --scratch-path /tmp/cs-46`）。
- **D** 回归：其余 40 条断言**零改动、零删除**；`defaults suite isolation`、`history limit persistence`、`history limit default` 仍通过；`~/Library/Preferences/` 下 `ClipShelf.plist` / `local.codex.*` / `ClipShelf.Runtime43.*` 的 **mtime 前后一致**（给出实测时间戳）。
- **E** 报告：`prompts/prompt-46-self-test-defaults-set-based-assertion-report.md`，含改动清单、A 的 3 次输出、**B 的变异前后对照**、新判据的稳定性实测（连续 5 次）。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Assert self-test defaults stay on a single fixed domain`
