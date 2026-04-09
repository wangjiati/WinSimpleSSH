# 01 — 客户端现状分析

> **目的**：在动手之前搞清楚当前客户端是怎么工作的，找出阻碍"作为 CLI 使用"的关键位置。
> **日期**：2026-04-09
> **相关代码版本**：`main` @ `355d14d`（注：CLAUDE.md 里写的 `804abe7` 基线已被 6 个 commit 超过，主要变化：上传握手预检 `UploadReady`、线程安全 `_sendLock/SafeSend`、FileTransfer 拆分 `SendUploadStart`/`SendUploadChunks`、日志系统）
> **⚠️ 本文档的行号在代码持续迭代后会失效**：改代码前以实际 Read 到的内容为准，不要依赖本文里的 `file.cs:123` 引用

## TL;DR

1. 客户端**完全是交互式设计**：密码输入和命令输入都用 `Console.ReadKey(intercept: true)`，无法通过管道 stdin 喂数据
2. 认证是异步的：`WaitOne(10000)` 等一个由 OnMessage 回调触发的 `ManualResetEvent`
3. 服务端的 cmd.exe 是**持久进程**，协议里**没有"命令执行完毕"的信号**——这是非交互模式最大的难点
4. 协议层很干净（`ProtocolMessage { type, data }` + 一堆 JSON payload），改造不涉及协议字段改动

## 代码拓扑

```
SSHClient/
├── Program.cs          ← 入口：arg 解析 + 交互主循环 + Console.ReadKey
├── Core/
│   ├── RemoteShell.cs  ← WebSocket 连接、心跳、消息收发、输出回调
│   └── FileTransfer.cs ← 上传/下载（本次不改动）
```

## 关键位置

### 1. Arg 解析（Program.cs:31-65）

当前只认三个开关：`<host>` / `-p <port>` / `-u <username>`。

```csharp
int i = 1;
while (i < args.Length)
{
    if (args[i] == "-p" && i + 1 < args.Length) { ... }
    else if (args[i] == "-u" && i + 1 < args.Length) { ... }
    else if (host == null) { host = args[i]; i++; }
    else { i++; }  // 未知参数被吃掉
}
```

**改造点**：非交互相关的新开关（`-P`、`-e`、`--password-stdin` 等）要插入到这个 while 循环里。未知参数被静默吞掉这一点也该顺手修掉，否则用户拼错参数名会没报错。

### 2. 密码输入（Program.cs:67-68, 283-308）

```csharp
Console.Write("Password: ");
password = ReadPassword();  // 内部循环调用 Console.ReadKey(true)
```

**阻塞点**：`Console.ReadKey` 在 stdin 被重定向时会抛 `InvalidOperationException`。非交互模式必须**完全跳过** `ReadPassword()` 的调用，不能"降级"。

### 3. 认证 → Shell 循环（Program.cs:70-177）

```csharp
var shell = new RemoteShell();
var authResult = new ManualResetEvent(false);

shell.SetOutputHandler(signal => {
    if (signal == "AUTH_OK" || signal == "AUTH_FAIL") authResult.Set();
});

shell.Connect(host, port, username, password);
authResult.WaitOne(10000);   // ← 异步等认证结果

if (!shell.IsConnected) { ... return; }

shell.StartHeartbeat();

// Interactive loop
while (shell.IsConnected)
{
    var input = ReadLineWithHistory(...);  // ← 又一个 ReadKey 阻塞点
    shell.SendInput(input + "\n");
}
```

**注意**：`SetOutputHandler` 被**调用了两次**（Program.cs:73 和 :79），第二次覆盖了第一次——这是个已有的小 bug，但当前能工作是因为第二次的 handler 里也处理了 `AUTH_OK/AUTH_FAIL`。非交互模式改造时顺手合并成一个 handler 比较干净。

### 4. Shell 输出流（RemoteShell.cs:108-116）

```csharp
case MessageType.ShellOutput:
    var output = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
    Console.Write(output.Text);   // ← 直接往 stdout 写
    break;
```

**问题**：所有 ShellOutput 都被**直接写到 Console**，没有经过 `_onOutput` 回调。非交互模式需要能**捕获输出**（为了检测"命令结束标记"），所以这里要改成既写 Console 又触发回调，或者让 `_onOutput` 拦截掉。

### 5. 服务端 Shell 会话（ShellSession.cs:21-54）

```csharp
_process = new Process {
    StartInfo = new ProcessStartInfo("cmd.exe") {
        RedirectStandardInput = true,
        ...
    }
};
_process.Start();
// 两个后台线程持续读 cmd.exe 的 stdout/stderr，转发到客户端
```

**关键**：服务端 cmd.exe 是**一个长期运行的进程**，从启动到 Disconnect 之间持续接收命令。这意味着：

- 客户端发过去的每条命令（以 `\n` 结尾）都会被 cmd.exe 执行
- cmd.exe 的输出流**不区分命令边界**——`dir` 的输出和下一条命令的 prompt 混在一起
- **协议里没有任何"命令 N 执行完成"的消息类型**（见 `MessageType` 枚举）

这就是非交互模式必须解决的核心问题：**如何知道一条命令执行完了，可以退出？**

## 核心难点：命令完成检测

### 为什么不能"等输出停了就退出"

- cmd.exe 的 prompt `C:\...>` 也是输出，会在命令结束后打印
- 但"等待没新输出 N 毫秒"不可靠：慢命令（`ping -n 10 ...`）会被误判结束
- 慢网络或大输出也可能出现短暂停顿

### 三种可行方案

| 方案 | 做法 | 优点 | 缺点 |
|------|------|------|------|
| **A. 标记注入** | 发送 `user_cmd & echo __SSHC_DONE_%ERRORLEVEL%__`，客户端监听流里出现这个标记 | 无需改服务端；能拿到 %ERRORLEVEL% | 标记可能被污染（命令输出恰好包含该字符串）；需要 escape |
| **B. exit 后等断开** | 发送 `user_cmd`，然后发 `exit`；等 cmd.exe 退出导致 WebSocket 断连 | 最简单 | 拿不到 %ERRORLEVEL%；每次都要重建 cmd.exe |
| **C. 协议扩展** | 服务端增加 `CommandComplete` 消息，但 cmd.exe 不会告诉服务端"命令结束了"，所以服务端也得用标记注入检测 | 协议干净 | 工作量最大；本质上还是方案 A 只是检测放在服务端 |

**推荐**：方案 A。业界成熟做法（Ansible 的 `raw` 模块、大量运维脚本都用这个套路）。

## 其他已读文件

- `SSHCommon/Protocol/MessageType.cs` — 协议枚举，本次**不需要改动**
- `SSHCommon/Protocol/ProtocolMessage.cs` — 消息基类和 DTO，**不需要改动**
- `SSHClient/Core/FileTransfer.cs` — 本次非交互模式**不涉及**文件传输

## 下一步

→ [02-design-decisions.md](./02-design-decisions.md) — 把上面列出的决策点交给用户拍板
