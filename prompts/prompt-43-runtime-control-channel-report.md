# prompt-43 运行时控制通道 - 实施报告

## 结果

已增加仅在隔离环境变量完整配置时启用的本地 Unix domain socket 控制通道。脚本现在可以驱动一个正在运行的隔离 ClipShelf 实例，执行探活、状态读取、内存记录导出、具名剪贴板文本注入、历史上限修改、历史清空和优雅退出。

默认启动路径保持不变：未设置 `CLIPSHELF_CONTROL_SOCKET` 时不创建任何控制端点，仍监听系统剪贴板。启用控制端点时，必须同时设置独立数据目录、独立偏好域和具名剪贴板；socket 必须位于独立数据目录内，权限为 `0600`。

## 改动文件

- `Sources/ClipShelfLite/Support/AppEnvironment.swift`
  - 增加具名剪贴板和隔离 socket 环境接缝及安全校验。
- `Sources/ClipShelfLite/Support/RuntimeControlProtocol.swift`
  - 增加 JSON 协议、Unix socket 传输、CLI 客户端与参数校验。
- `Sources/ClipShelfLite/Services/RuntimeControlServer.swift`
  - 增加后台 socket 服务、命令分发、权限管理和退出清理。
- `Sources/ClipShelfLite/Stores/ClipStore.swift`
  - 剪贴板来源改为可注入接缝；增加控制通道专用文本注入入口。
- `Sources/ClipShelfLite/App/AppDelegate.swift`
  - 仅在环境完整配置时启动控制服务，退出时停止服务并移除 socket。
- `Sources/ClipShelfLite/main.swift`
  - 增加 `--ctl` 客户端模式，保留原有 `--self-test` 行为。
- `Sources/ClipShelfLite/Support/SelfTest.swift`
  - 增加端点默认关闭、连接失败非零退出和具名剪贴板隔离断言。
- `README.md`
  - 增加中英文隔离运行时控制说明与命令示例。

## 最终命令表

所有命令均要求客户端进程具有与服务端相同的四个环境变量：`CLIPSHELF_DATA_DIR`、`CLIPSHELF_DEFAULTS_SUITE`、`CLIPSHELF_PASTEBOARD_NAME`、`CLIPSHELF_CONTROL_SOCKET`。

| 命令 | 作用 |
| --- | --- |
| `ClipShelf --ctl ping` | 返回存活状态和版本 |
| `ClipShelf --ctl status` | 返回内存记录数、历史上限、记录开关和数据目录 |
| `ClipShelf --ctl export <path>` | 将内存中的记录导出为 JSON |
| `ClipShelf --ctl inject-text <text>` | 向实例监听的具名剪贴板写入文本 |
| `ClipShelf --ctl inject-text <text> --type <pasteboard-type>` | 写入文本并附加指定 pasteboard 类型标记 |
| `ClipShelf --ctl set-limit <number>` | 走 `ClipStore.setHistoryLimit(_:)` 修改上限并立即裁剪落盘 |
| `ClipShelf --ctl clear` | 走 `ClipStore.clearHistory()` 清空内存与历史文件 |
| `ClipShelf --ctl quit` | 返回结果后让实例正常退出 |

成功返回退出码 `0` 和 JSON；服务端返回失败时退出码为 `1`；参数、环境或连接错误退出码为 `2`，同时输出含可读错误的 JSON。

## 自动测试

### 构建与共享自测

- `swift build`：通过。
- `swift test`：通过，16/16 checks passed。
- `ClipShelf --self-test <报告路径>`：退出码 0，16/16 checks passed。
- 新增断言覆盖：控制端点默认关闭、连接不存在的端点返回非零错误码、具名剪贴板与系统/其它剪贴板隔离。

### 运行实例端到端

实际启动隔离实例并通过 `--ctl` 驱动，结果如下：

- socket 权限：`0600`。
- 初始状态：0 条，上限 100。
- 连续注入 8 条后：内存 8 条，导出 JSON 8 条。
- `set-limit 5` 后：返回内存 5 条，`history.json` 立即为 5 条。
- `clear` 后：内存与 `history.json` 均为 0 条。
- `quit` 后：进程正常退出，socket 被移除。
- 未配置 socket 的隔离实例：没有创建或监听控制 socket。
- 向系统剪贴板写入内容：具名剪贴板实例仍为 0 条。
- 向具名剪贴板注入 `org.nspasteboard.ConcealedType`：仍为 0 条。
- 向具名剪贴板注入普通文本：增加为 1 条。
- 测试前后真实 `history.json` SHA-256 相同：`e6eb9e4d0fd800e13625d257094ca32f385a5028f67cb02d37ce0b821ba1d2e1`。
- 标准偏好域中的 `history.maxItems` 与 `clipboardHistory.enabled` 前后相同。
- 系统剪贴板已在测试结束时按原始类型和数据恢复。

## 可直接复现的脚本

```bash
#!/usr/bin/env bash
set -euo pipefail

BIN="$(swift build --show-bin-path)/ClipShelf"
ROOT="$(mktemp -d /tmp/clipshelf-control.XXXXXX)"
export CLIPSHELF_DATA_DIR="$ROOT/data"
export CLIPSHELF_DEFAULTS_SUITE="ClipShelf.Control.$RANDOM"
export CLIPSHELF_PASTEBOARD_NAME="ClipShelf.Control.Pasteboard.$RANDOM"
export CLIPSHELF_CONTROL_SOCKET="$ROOT/data/control.sock"
mkdir -p "$CLIPSHELF_DATA_DIR"

"$BIN" >"$ROOT/app.log" 2>&1 &
APP_PID=$!
trap 'kill "$APP_PID" 2>/dev/null || true' EXIT

while [ ! -S "$CLIPSHELF_CONTROL_SOCKET" ]; do sleep 0.1; done
"$BIN" --ctl ping
"$BIN" --ctl inject-text "runtime test"
sleep 0.6
"$BIN" --ctl status
"$BIN" --ctl export "$ROOT/history-export.json"
"$BIN" --ctl set-limit 5
"$BIN" --ctl clear
"$BIN" --ctl quit
wait "$APP_PID"
```

## 范围说明

- 未修改 WorkBuddy 所有的 `tests/parity/`。现有套件可以改用这四个隔离环境变量和 `--ctl` 命令驱动实例；所需能力已经由产品代码提供。
- 未新增第三方依赖、TCP 监听、开发者签名或公证能力。
- 未改变 `history.json` 的字段和格式。
- 控制服务运行在后台队列，未改变既有 0.45 秒剪贴板轮询周期。
