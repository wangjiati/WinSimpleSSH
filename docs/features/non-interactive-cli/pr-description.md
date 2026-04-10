# PR 提交材料

> 本文档是提交给上游 `wangjiati/WinSimpleSSH` 的 Pull Request 内容备份。
> 提交时间：2026-04-09
> 提交人：xxf66666

---

## 标题（Title）

```
feat: 添加非交互 CLI 模式（exec/start/upload/download 动词）
```

---

## 描述（Description）

```markdown
## Summary

给 SSHC 客户端新增 4 个非交互动词，让 AI Agent 和脚本可以通过 `subprocess` 调用 SSHC 做远程控制。原有的 `SSHC connect` 交互式 REPL 完全无回归。

原动机:factory 场景下服务器上的 AI Agent 要批量操作设备机(启动软件、派发文件、查询状态),需要 CLI 能一次性执行并返回结构化结果。纯交互式 SSHC 无法被 Agent 调用(卡在密码提示和 `Console.ReadKey` 上)。

## What's new

- **4 个非交互动词**:
  - `SSHC exec <host> -u <user> -P <pwd> "<command>"` — 执行远程命令,等待完成,返回 `%ERRORLEVEL%`
  - `SSHC start <host> -u <user> -P <pwd> "<program>"` — fire-and-forget 启动远程 GUI 程序
  - `SSHC upload <host> -u <user> -P <pwd> <local> <remote>` — 上传文件
  - `SSHC download <host> -u <user> -P <pwd> <remote> <local>` — 下载文件
- **`--json` 输出模式**:Agent 友好的结构化结果,schema 含 `ok / host / verb / exit_code / stdout / stderr / duration_ms / error / timestamp`
- **退出码规范(仿 OpenSSH)**:`0` 成功 / `130` Ctrl+C / `253` 协议错误 / `254` 认证失败 / `255` 连接失败 / 其他透传远程 `%ERRORLEVEL%`
- **UTF-8 stdout**(仅非交互模式) + **banner 全部走 stderr**,`SSHC ... > out.txt` 永远只含命令真实输出
- **Ctrl+C 看门狗**:转发 Interrupt 给远端 + 3 秒硬退兜底
- **WebSocketSharp 内置 Fatal 日志抑制**(`_ws.Log.Output = no-op`),连接失败时错误输出干净

## Key design decisions

1. **动词式 CLI**(`SSHC exec`)而不是子命令式(`SSHC connect -e "..."`)—— Agent 友好,每个动作一个动词,不用记复杂参数组合
2. **两阶段标记协议** 解决 cmd.exe 在 stdin 被管道化时 `@echo off` 失效的命令回显污染问题:
   - 发送 `echo <begin-marker>\n<user-cmd>\necho <end-marker>-%ERRORLEVEL%__\n`
   - `OutputCapture` 用行首锚点正则 `(?m)^<marker>` 匹配,只命中真实输出行
   - 客户端按行剥离回显命令,stdout 最终干净
3. **每次调用都是无状态的**:连接 → 认证 → 执行 → 断开,在一个 SSHC 进程生命周期内完成。Agent 并行调用由多进程承载,进程间无共享状态
4. **`start` 动词的 fire-and-forget 兜底**:5 秒短超时内如果 begin marker 已出现(cmd.exe 确认收到命令),假定 GUI 已 detach 返回 0。避免 UWP 桩或缺失可执行文件时 SSHC 永久阻塞
5. **保留交互式 `connect` REPL 完全不动**,只是在 `Main` 入口加了 verb 分派

## Code changes

- 新增 4 个文件:
  - `src/SSHClient/Core/ExitCodes.cs` — 退出码常量
  - `src/SSHClient/Core/CommandResult.cs` — JSON 结果 DTO
  - `src/SSHClient/Core/OutputCapture.cs` — 两阶段标记扫描 + 线程安全 buffer
  - `src/SSHClient/Core/NonInteractiveRunner.cs` — 4 个动词的流程编排
- `src/SSHClient/Core/RemoteShell.cs`:新增 `SetShellOutputHandler` 拦截接口;`SetOutputHandler` → `SetSignalHandler` 语义重命名;banner 输出改走 stderr
- `src/SSHClient/Core/FileTransfer.cs`:加 `Quiet` 静态开关;进度条/辅助文字走 stderr
- `src/SSHClient/Program.cs`:`Main` 返回 int(为支持 CLI 退出码),verb 分派入口,抽出 `RunConnect`(原交互主体),新增 `RunNonInteractive`/`ParseCommonArgs`

## Test plan

验证环境:Windows Sandbox 作为远端(WDAGUtilityAccount),本机 SSHC 作为客户端

- [x] `exec "echo hello"` → stdout 干净,exit 0
- [x] `exec "echo 你好世界"` → UTF-8 字节正确(12 字节)
- [x] `exec "cmd /c exit 42"` → exit 42 透传
- [x] `exec --json "echo test"` → 合法 JSON,`stdout` 字段干净
- [x] 错密码 → exit 254
- [x] 错 IP → exit 255
- [x] `ping -n 3` → 2.77s 完整输出
- [x] `start calc.exe` → 0.7s 正常路径
- [x] `start notepad.exe`(不存在时) → 5.6s fire-and-forget 兜底
- [x] `upload` 138 字节文件 → 成功
- [x] `download` round-trip → diff 1:1 一致
- [x] `--json` 模式下 stdout 纯净 JSON,banner 全走 stderr
- [x] 交互模式 REPL 完全无回归:login / dir 中文 / ping / clients / kick / exit

## Design docs

完整的设计过程文档在 `docs/features/non-interactive-cli/`,包括:
- `00-context.md` — 业务场景(factory + Agent)
- `01-current-state.md` — 改造前的代码现状分析
- `02-design-decisions.md` — 9 个设计决策点及取舍
- `03-change-list.md` — 精确改动清单
- `04-implementation-plan.md` — 分阶段落地 + 进度日志

## Compatibility

- **.NET Framework 4.5.2** 不变
- **Win7/Win10/Win11** 兼容性不变
- **协议层(SSHCommon)零改动** —— 服务端完全不需要升级,老服务端和新客户端完全互通
- **交互模式行为 100% 向后兼容**
```

---

## 提交操作步骤

1. **清空**当前 "Add a title" 框里的 `Feature/non interactive cli`(GitHub 自动生成的)
2. **粘贴**上面的标题到 "Add a title"
3. **点击** "Add a description" 的文本框
4. **粘贴**上面"描述"部分的 markdown 内容(**只复制 markdown 代码块里面的内容**,不包括外层三个反引号)
5. **保持** "Allow edits by maintainers" 勾选(让 wangjiati 有能力在 PR 上直接修改你的代码,常见礼貌)
6. **点** 绿色的 `Create pull request` 按钮(不要点旁边的 `Create draft pull request`)
