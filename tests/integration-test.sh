#!/bin/bash
# WinSimpleSSH 端到端集成测试
# 测试：exec、upload、download、update

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SERVER_DIR="$ROOT_DIR/src/SSHServer/bin/Debug/net452"
CLIENT_DIR="$ROOT_DIR/src/SSHClient/bin/Debug/net452"
TEST_DIR="$ROOT_DIR/test_workspace"
PORT=22222
USER="admin"
PASS="admin123"
PASS_COUNT=0
FAIL_COUNT=0
SERVER_PID=""

# === 工具函数 ===
pass() { PASS_COUNT=$((PASS_COUNT + 1)); echo "  [PASS] $1"; }
fail() { FAIL_COUNT=$((FAIL_COUNT + 1)); echo "  [FAIL] $1"; }
section() { echo ""; echo "=== $1 ==="; }

# === 清理旧进程 ===
cleanup() {
    section "Cleanup"
    # 杀掉测试工作区启动的 SSHServer
    if [ -n "$SERVER_PID" ]; then
        kill "$SERVER_PID" 2>/dev/null || true
    fi
    wait 2>/dev/null || true

    # 清理测试目录
    rm -rf "$TEST_DIR"
    echo "  Test workspace cleaned."
}
trap cleanup EXIT

# === 编译 ===
section "Build"
cd "$ROOT_DIR"
dotnet build WinSimpleSSH.sln -v q 2>&1 | tail -3

# === 准备测试环境 ===
section "Setup test workspace"
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR"

# 复制服务端文件（排除 log 目录）
cp "$SERVER_DIR"/*.exe "$SERVER_DIR"/*.dll "$SERVER_DIR"/*.json "$SERVER_DIR"/*.bat "$SERVER_DIR"/*.config "$TEST_DIR/" 2>/dev/null || true

# 复制客户端
cp "$CLIENT_DIR/SSHC.exe" "$TEST_DIR/"

# 删除自动生成的 server.json，让服务端自动创建默认配置
rm -f "$TEST_DIR/server.json"

echo "  Files prepared."

# === 启动服务端 ===
section "Start SSHServer"
cd "$TEST_DIR"
./SSHServer.exe &
SERVER_PID=$!
cd "$ROOT_DIR"

# 等待服务端就绪（最多 5 秒）
echo "  Waiting for server to start..."
for i in $(seq 1 10); do
    if ./src/SSHClient/bin/Debug/net452/SSHC.exe exec 127.0.0.1 -u "$USER" -p "$PASS" "echo ready" 2>/dev/null | grep -q "ready"; then
        echo "  Server ready (PID=$SERVER_PID)."
        break
    fi
    sleep 0.5
done

# === 测试 1: exec 命令 ===
section "Test 1: exec command"
OUTPUT=$("$TEST_DIR/SSHC.exe" exec 127.0.0.1 -u "$USER" -p "$PASS" "echo hello_integration_test" 2>/dev/null || true)
if echo "$OUTPUT" | grep -q "hello_integration_test"; then
    pass "exec echo command"
else
    fail "exec echo command (output: $OUTPUT)"
fi

OUTPUT=$("$TEST_DIR/SSHC.exe" exec 127.0.0.1 -u "$USER" -p "$PASS" "echo %COMPUTERNAME%" 2>/dev/null || true)
if [ -n "$OUTPUT" ]; then
    pass "exec environment variable"
else
    fail "exec environment variable"
fi

# === 测试 2: upload + download ===
section "Test 2: upload and download"
TEST_CONTENT="integration test content $(date +%s)"
echo "$TEST_CONTENT" > "$TEST_DIR/test_upload.txt"

"$TEST_DIR/SSHC.exe" upload 127.0.0.1 -u "$USER" -p "$PASS" "$TEST_DIR/test_upload.txt" "test_remote.txt" 2>&1 | grep -qi "upload\|上传" && pass "upload file" || fail "upload file"

rm -f "$TEST_DIR/test_download.txt"
"$TEST_DIR/SSHC.exe" download 127.0.0.1 -u "$USER" -p "$PASS" "test_remote.txt" "$TEST_DIR/test_download.txt" 2>&1 || true
[ -f "$TEST_DIR/test_download.txt" ] && pass "download file" || fail "download file"

if [ -f "$TEST_DIR/test_download.txt" ]; then
    DL_CONTENT=$(cat "$TEST_DIR/test_download.txt")
    if [ "$DL_CONTENT" = "$TEST_CONTENT" ]; then
        pass "download content matches"
    else
        fail "download content mismatch (expected: $TEST_CONTENT, got: $DL_CONTENT)"
    fi
else
    fail "download file not found"
fi

# === 测试 3: 远程更新 ===
section "Test 3: remote update"

# 准备"新版本"文件（复制当前 exe）
cp "$TEST_DIR/SSHServer.exe" "$TEST_DIR/update_source.exe"
UPDATE_SOURCE=$(cygpath -w "$TEST_DIR/update_source.exe" 2>/dev/null || echo "$TEST_DIR/update_source.exe")

# 计算 MD5
if command -v md5sum &>/dev/null; then
    MD5=$(md5sum "$TEST_DIR/update_source.exe" | cut -d' ' -f1)
elif command -v certutil &>/dev/null; then
    MD5=$(certutil -hashfile "$TEST_DIR/update_source.exe" MD5 | grep -v ":" | tr -d ' \r\n' | tr 'A-Z' 'a-z')
else
    MD5=""
fi

echo "  Update source: $UPDATE_SOURCE"
echo "  MD5: ${MD5:-N/A}"

if [ -n "$MD5" ]; then
    UPDATE_OUTPUT=$("$TEST_DIR/SSHC.exe" update 127.0.0.1 -u "$USER" -p "$PASS" "--source" "$UPDATE_SOURCE" "--checksum" "$MD5" 2>&1 || true)
else
    UPDATE_OUTPUT=$("$TEST_DIR/SSHC.exe" update 127.0.0.1 -u "$USER" -p "$PASS" "--source" "$UPDATE_SOURCE" 2>&1 || true)
fi

echo "  Response: $UPDATE_OUTPUT"

if echo "$UPDATE_OUTPUT" | grep -qi "accepted\|staged"; then
    pass "update request accepted"
else
    fail "update request (response: $UPDATE_OUTPUT)"
fi

# 等待服务端处理更新并退出
sleep 3

# 验证更新产物
if [ -f "$TEST_DIR/update_staging/SSHServer.exe" ]; then
    pass "update staging file created"
else
    fail "update staging file NOT found"
fi

if [ -f "$TEST_DIR/update_marker" ]; then
    pass "update marker file created"
else
    fail "update marker file NOT found"
fi

# 验证服务端进程已退出（因更新而终止）
if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    pass "server process exited after update"
    SERVER_PID=""  # 避免 cleanup 再尝试 kill
else
    fail "server process still running after update"
fi

# === 结果 ===
section "Results"
TOTAL=$((PASS_COUNT + FAIL_COUNT))
echo "  Total: $TOTAL  Passed: $PASS_COUNT  Failed: $FAIL_COUNT"
echo ""

if [ "$FAIL_COUNT" -eq 0 ]; then
    echo "  ALL TESTS PASSED"
    exit 0
else
    echo "  SOME TESTS FAILED"
    exit 1
fi
