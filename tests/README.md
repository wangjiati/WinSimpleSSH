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
└── README.md                          # 本文件
```
