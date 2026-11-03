#!/usr/bin/env bash
set -euo pipefail

mode=$1
root=$2
health_url=$3
revision=$4
digest=$5
project=bgi-coordinator
service=coordinator
image=bgi-coordinator-coordinator
previous="$image:deploy-previous"
compose=(docker compose --project-name "$project" -f docker-compose.yml -f docker-compose.override.yml)

say() { printf '%s\n' "$*"; }
fail() { printf '[失败] %s\n' "$*" >&2; exit 1; }

cd "$root"
[[ $(pwd -P) == "$root" && -f docker-compose.yml && -f docker-compose.override.yml ]] ||
    fail "部署目录或现网 Compose 文件不匹配。"
for utility in docker curl flock tar sha256sum mktemp timeout; do
    command -v "$utility" >/dev/null || fail "服务器缺少 $utility。"
done

current_container() { "${compose[@]}" ps -q "$service"; }
container_format() { docker inspect --type container --format "$1" "$2"; }
image_format() { docker image inspect --format "$1" "$2"; }

health() {
    curl -fsS --max-time 4 --noproxy '*' "$health_url" 2>/dev/null |
        grep -Eq '"status"[[:space:]]*:[[:space:]]*"BgiCoordinatorServer running"'
}

check_config() {
    # config --hash resolves the running password without dumping the effective config.
    [[ $("${compose[@]}" config --hash "$service") == "$service $config_hash" &&
       $("${compose[@]}" config --images "$service") == "$image" ]]
}

inspect_target() {
    container=$(current_container)
    [[ $container =~ ^[[:xdigit:]]{12,64}$ ]] || fail "未找到唯一运行中的协调容器。"
    expected="$project|$service|$root|$root/docker-compose.yml,$root/docker-compose.override.yml|$image"
    actual=$(container_format '{{index .Config.Labels "com.docker.compose.project"}}|{{index .Config.Labels "com.docker.compose.service"}}|{{index .Config.Labels "com.docker.compose.project.working_dir"}}|{{index .Config.Labels "com.docker.compose.project.config_files"}}|{{.Config.Image}}' "$container")
    [[ $actual == "$expected" ]] || fail "运行容器不属于预期的 Compose 项目。"
    config_hash=$(container_format '{{index .Config.Labels "com.docker.compose.config-hash"}}' "$container")
    [[ $config_hash =~ ^[[:xdigit:]]{64}$ ]] || fail "无法识别当前容器配置。"
    old_image=$(container_format '{{.Image}}' "$container")
    [[ $old_image =~ ^sha256:[[:xdigit:]]{64}$ ]] || fail "无法识别当前镜像。"
    old_revision=$(image_format '{{with index .Config.Labels "org.opencontainers.image.revision"}}{{.}}{{end}}' "$old_image")
    ADMIN_MONITOR_PASSWORD=$(container_format '{{range .Config.Env}}{{println .}}{{end}}' "$container" |
        sed -n 's/^ADMIN_MONITOR_PASSWORD=//p')
    export ADMIN_MONITOR_PASSWORD
    check_config || fail "服务器 Compose 配置与运行容器不一致，请先核对服务器配置。"
    health || fail "现网健康检查未通过。"
}

if [[ $mode == check ]]; then
    inspect_target
    [[ -w . ]] || fail "SSH 用户没有部署目录写权限。"
    say "[通过] SSH、现网 Compose 和 /health 检查正常。"
    say "现网版本：${old_revision:-现网镜像未标注提交号}（镜像 $old_image）"
    exit 0
fi

[[ $mode == deploy && $revision =~ ^[a-f0-9]{40}$ && $digest =~ ^[a-f0-9]{64}$ ]] ||
    fail "部署参数无效。"
# Lock the deployment directory itself; checking the server never creates a file.
exec 9<.
flock -n -x 9 || fail "已有部署正在执行，请等它结束。"
inspect_target
if [[ $old_revision == "$revision" ]]; then
    say "[通过] 此提交已在运行，服务正常，无需重启。"
    exit 0
fi

work=$(mktemp -d /tmp/bgi-coordinator-deploy.XXXXXXXX)
[[ $work =~ ^/tmp/bgi-coordinator-deploy\.[[:alnum:]]{8}$ ]] || fail "临时目录路径异常。"
candidate="$image:deploy-candidate-${work##*.}"
switch_started=0
recreate() { timeout 120 "${compose[@]}" up -d --no-deps --no-build --pull never --force-recreate "$service"; }

wait_for_image() {
    local expected_image=$1 current actual_image current_hash attempt
    for ((attempt=0; attempt<30; attempt++)); do
        current=$(current_container) || current=
        if [[ $current =~ ^[[:xdigit:]]{12,64}$ ]]; then
            actual_image=$(container_format '{{.Image}}' "$current") || actual_image=
            current_hash=$(container_format '{{index .Config.Labels "com.docker.compose.config-hash"}}' "$current") || current_hash=
            if [[ $actual_image == "$expected_image" && $current_hash == "$config_hash" ]] && health; then
                return 0
            fi
        fi
        sleep 2
    done
    return 1
}

cleanup() {
    local status=$?
    trap - EXIT
    trap '' HUP INT TERM PIPE
    if (( switch_started )); then
        say "[回退] 更新未通过，正在恢复原镜像。" || true
        if docker image tag "$old_image" "$image" >/dev/null 2>&1 &&
           check_config &&
           { { [[ $(current_container) == "$container" ]] && health; } ||
             { recreate >"$work/rollback.log" 2>&1 && wait_for_image "$old_image"; }; }; then
            say "[已回退] 原镜像的健康检查已通过。" || true
        else
            say "[需检查] 未能确认回退成功。旧镜像保存在 $previous（$old_image）。" || true
        fi
    fi
    docker image rm "$candidate" >/dev/null 2>&1 || true
    rm -rf -- "$work" || true
    exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 141' PIPE

say "[1/4] 接收并校验提交 $revision"
cat > "$work/source.tar.gz"
printf '%s  %s\n' "$digest" "$work/source.tar.gz" | sha256sum --check --status ||
    fail "上传包校验失败，现网未切换。"
mkdir "$work/source"
tar -xzf "$work/source.tar.gz" --no-same-owner -C "$work/source"
for required in Dockerfile BgiCoordinatorServer.csproj LICENSE README.md THIRD-PARTY-NOTICES.md; do
    [[ -s $work/source/$required ]] || fail "部署包缺少 $required，现网未切换。"
done

say "[2/4] 构建新镜像，现网继续运行"
docker build --build-arg "SOURCE_REVISION=$revision" --label "org.opencontainers.image.revision=$revision" \
    --tag "$candidate" "$work/source" </dev/null || fail "镜像构建失败，现网未切换。"
new_image=$(image_format '{{.Id}}' "$candidate")
[[ $new_image =~ ^sha256:[[:xdigit:]]{64}$ ]] || fail "无法确认新镜像，现网未切换。"
[[ $(current_container) == "$container" ]] && check_config && health ||
    fail "构建期间容器、配置或健康状态发生变化，现网未切换。"

say "[3/4] 切换协调服务"
docker image tag "$old_image" "$previous"
switch_started=1
docker image tag "$new_image" "$image"
recreate || fail "新容器启动失败。"

say "[4/4] 确认新镜像及健康接口"
wait_for_image "$new_image" || fail "新版本没有通过健康检查。"
switch_started=0
say "[成功] 已部署 $revision，协调服务健康检查通过。"
