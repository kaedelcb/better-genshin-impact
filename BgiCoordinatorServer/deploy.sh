#!/bin/bash
# 一键部署脚本：构建并重启容器
#
# 用法（在脚本所在目录执行即可）：
#   bash deploy.sh
#
# 前提：
#   - docker-compose.yml 与本脚本在同一目录
#   - 源码已通过其他方式（文件上传 / scp / rsync 等）同步到当前目录
#
# 首次运行会自动生成 docker-compose.override.yml（默认 8080:80），
# 如需修改宿主机端口，编辑该文件后重新运行即可。

# CRLF 自愈：本脚本若在 Windows 上编辑过，行尾可能是 CRLF（\r\n），
# 直接在 Linux 上跑会报 $'\r': command not found / set: invalid option name。
# 这里检测自身是否含 \r，若有则就地转成 LF 并用干净副本重新执行一次（仅自愈一次，避免递归）。
if [ -z "${__DEPLOY_LF_FIXED:-}" ] && grep -q $'\r' "$0" 2>/dev/null; then
  tmp="$(mktemp)"
  sed 's/\r$//' "$0" > "$tmp"
  export __DEPLOY_LF_FIXED=1
  exec bash "$tmp" "$@"
fi

set -euo pipefail

# 切到脚本所在目录，确保 docker compose 能找到 yml 文件
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

if [ ! -f docker-compose.yml ]; then
  echo "[错误] 当前目录未找到 docker-compose.yml: $SCRIPT_DIR"
  exit 1
fi

# docker-compose.yml 从当前进程环境读取管理员监控密码；旧脚本未传入时管理页会返回 503。
# 交互式部署时只在内存中输入，不把密码写进仓库、compose 文件或命令历史。
if [ -z "${ADMIN_MONITOR_PASSWORD:-}" ] && [ -t 0 ]; then
  printf '==> [管理员监控] 请输入至少 16 位的管理员密码（输入不回显；留空则关闭监控页）: '
  IFS= read -r -s ADMIN_MONITOR_PASSWORD
  printf '\n'
fi
if [ -n "${ADMIN_MONITOR_PASSWORD:-}" ]; then
  if [ "${#ADMIN_MONITOR_PASSWORD}" -lt 16 ]; then
    echo "[错误] ADMIN_MONITOR_PASSWORD 不足 16 位，未开始部署。"
    exit 1
  fi
  export ADMIN_MONITOR_PASSWORD
else
  echo "==> [提示] 未提供管理员密码；监控页保持关闭。"
fi

# 首次运行时自动生成 override 文件（覆盖宿主机端口为 8080，避免 80 占用冲突）
# 使用 !override 标签强制替换（不是追加）主 compose 文件的 ports 列表，
# 否则 docker compose 默认会把两边的 ports 合并，仍会尝试绑定 80。
if [ ! -f docker-compose.override.yml ]; then
  cat > docker-compose.override.yml <<'EOF'
# 本机/服务器特定的 docker compose 覆盖配置（不进 git 跟踪）。
# docker compose 命令会自动合并 docker-compose.yml + docker-compose.override.yml。
# !override 标签让本文件的 ports 列表完全替换主文件的 ports，而不是追加。
services:
  coordinator:
    ports: !override
      # 宿主机端口:容器内端口（容器内固定 80）
      - "8080:80"
EOF
  echo "==> [初始化] 已生成 docker-compose.override.yml（默认端口 8080）"
  echo "            如需改端口，编辑该文件后再运行本脚本。"
fi

echo "==> [1/3] 构建并重启容器（自动合并 docker-compose.yml + docker-compose.override.yml）"
docker compose up -d --build --force-recreate

echo "==> [2/3] 当前容器状态"
docker compose ps

echo "==> [3/3] 部署完成"
if [ -n "${ADMIN_MONITOR_PASSWORD:-}" ]; then
  echo "==> 管理页地址：http://www.autobgi.cn:8080/admin"
fi
