# 测试用例

使用前将 `HOST`、`USER`、`PASS` 替换为实际值。

运行顺序建议：先运行非交互模式测试（验证连通性），再运行交互模式测试。

```
tests/
├── 01-non-interactive-exec.bat        # 非交互：exec 基本命令
├── 02-non-interactive-exec-json.bat   # 非交互：exec --json 输出
├── 03-non-interactive-exec-env.bat    # 非交互：环境变量传密码
├── 04-non-interactive-start.bat       # 非交互：start 启动程序
├── 05-non-interactive-upload.bat      # 非交互：上传文件
├── 06-non-interactive-download.bat    # 非交互：下载文件
├── 07-non-interactive-port.bat        # 非交互：--port 指定端口
├── 08-non-interactive-quiet.bat       # 非交互：-q 静默模式
├── 09-interactive-password.bat        # 交互：密码提示输入
├── 10-interactive-auto.bat            # 交互：-p 免密启动
├── 11-interactive-env.bat             # 交互：环境变量免密启动
├── 12-error-auth.bat                  # 异常：错误密码
├── 13-error-connect.bat               # 异常：不可达主机
├── 14-error-args.bat                  # 异常：参数缺失
├── 15-encoding-restore.bat            # 非交互退出后控制台代码页恢复（无需 server）
├── 16-server-help.bat                 # SSHServer help 各变体（-h、--help、-?、/?）打印用法即退出（无需 server）
├── 17-interactive-disconnect.ps1      # 交互及 exec：服务端断开后及时退出（自建临时本地服务端）
└── README.md                          # 本文件
```

断线回归测试先构建项目，再运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/17-interactive-disconnect.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/17-interactive-disconnect.ps1 -Mode Exec
powershell -NoProfile -ExecutionPolicy Bypass -File tests/17-interactive-disconnect.ps1 -Mode ExecJson
powershell -NoProfile -ExecutionPolicy Bypass -File tests/17-interactive-disconnect.ps1 -Mode Upload
powershell -NoProfile -ExecutionPolicy Bypass -File tests/17-interactive-disconnect.ps1 -Mode MessageRace
```

`Exec` 和 `ExecJson` 模式通过远端命令终止临时服务端，验证客户端及时以 `255` 退出并保留断线前的输出。

`Upload` 验证正常上传、下载及上传中途断线，临时创建一个逻辑大小为 2 GB 的文件并在结束后删除。`MessageRace` 验证断线不会抢在正在处理的输出回调前结束等待，需要 .NET Framework 自带的 C# 编译器来构建测试辅助类。
