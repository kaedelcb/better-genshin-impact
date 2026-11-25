"""Browser side of AuthorizedBrowserEditsCrossServerRoomAndChecksOriginalSwitchReceipt.

Run by the C# integration test, not a standalone gateway simulator. The test owns both
127.0.0.1 gateways and the assistant, and checks persisted data at our two checkpoints.
Requires Python Playwright and installed Edge (EDGE_PATH may override the usual path).
All interaction is through the real page controls; JS evaluations only read UI state.
"""

import json
import os
import sys
from urllib.parse import urlsplit

from playwright.sync_api import expect, sync_playwright

ROOM = "A12345"
OWNER_UID, A_PEER, B_PEER = "111111111", "222222222", "333333333"
INSTANCE = "cross-room-web-executor"
BOOK_B = "旁观乙房"
PROFILE_A, PROFILE_B = "web-a", "web-b"
NOTE = "隔离网页保存的乙房备注"


def checkpoint(stage, **data):
    print(json.dumps({"stage": stage, **data}, ensure_ascii=False), flush=True)
    if stage != "complete" and sys.stdin.readline().strip() != "continue":
        raise RuntimeError("C# fixture did not confirm the checkpoint")


def loopback_origin(url):
    parsed = urlsplit(url)
    if (parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or not parsed.port
            or parsed.username or parsed.password or parsed.path not in ("", "/")
            or parsed.query or parsed.fragment):
        raise ValueError("Only the two fixture loopback origins are supported")
    return f"http://127.0.0.1:{parsed.port}"


def choose_room_view(page, action, name=None):
    page.locator(".wr-trigger").click()
    option = page.locator(f'.wr-menu[open] [data-wr-action="{action}"]')
    if name is not None:
        option = option.filter(has_text=name)
    option.click()


def member(page, uid):
    return page.locator(f'#memberList .member-card[data-member-uid="{uid}"]')


def assert_online_members(page, *uids):
    expect(page.locator("#memberList .member-card.assistant-online")).to_have_count(len(uids))
    for uid in uids:
        expect(page.locator(f'#memberList .assistant-online[data-member-uid="{uid}"]')).to_be_visible()


def wait_room_membership(page, a, b, switched=False):
    # Both real room snapshots must be ready, with the same known executor at exactly one origin.
    page.wait_for_function("""({a,b,code,uid,instance,aPeer,bPeer,switched}) => {
        const rooms = window.webRooms;
        const main = rooms?.getMainContext();
        const left = rooms?.getContext(rooms.roomKey(a,code));
        const right = rooms?.getContext(rooms.roomKey(b,code));
        if (!main?.ready || main.serverUrl !== a || main.roomCode !== code ||
            !left?.ready || !right?.ready || !left.isMain || right.isMain) return false;
        const online = c => c.players.filter(p => p.online === true);
        const live = c => online(c).some(p => p.playerUid === uid && p.clientInstanceId === instance);
        return online(left).some(p => p.playerUid === aPeer) && online(right).some(p => p.playerUid === bPeer)
            && live(left) === !switched && live(right) === switched;
    }""", arg={"a": a, "b": b, "code": ROOM, "uid": OWNER_UID, "instance": INSTANCE,
                 "aPeer": A_PEER, "bPeer": B_PEER, "switched": switched}, timeout=65000 if switched else 15000)


def run(page, args, errors, non_fixture_requests):
    a, b = args["a"], args["b"]
    owner = page.locator("#roomOwnerView")
    step = "login to A"
    try:
        page.goto(a + "/", wait_until="domcontentloaded")
        page.locator("#roomCode").fill(ROOM)
        page.locator("#password").fill(args["aPassword"])
        page.locator("#nickname").fill("甲房队友")
        page.locator("#rememberMe").check()
        page.locator("#joinButton").click()
        expect(page.locator("#connectionText")).to_have_text("连接正常")
        assert_online_members(page, OWNER_UID, A_PEER)

        step = "add B to the browser room book"
        choose_room_view(page, "manage")
        manager = page.locator(".wr-manager[open]")
        manager.locator("[data-wr-add]").click()
        draft = manager.locator(".wr-profile-form")
        draft.locator('[name="name"]').fill(BOOK_B)
        draft.locator('[name="serverUrl"]').fill(b)
        draft.locator('[name="roomCode"]').fill(ROOM)
        draft.locator('[name="password"]').fill(args["bPassword"])
        draft.locator('[name="nickname"]').fill("乙房队友")
        draft.locator('[name="remember"]').check()
        draft.get_by_role("button", name="保存已有房间", exact=True).click()
        expect(manager.locator("[data-wr-result]")).to_contain_text("已保存")
        expect(manager.locator(".wr-book-row")).to_have_count(2)
        b_row = manager.locator(".wr-book-row").filter(has_text=BOOK_B)
        expect(b_row).to_contain_text(b)
        expect(b_row).to_contain_text(ROOM)
        expect(b_row.locator("[data-wr-status]")).to_have_attribute("data-state", "ready")
        expect(manager.locator(".wr-book-row").filter(has_text=a)).to_contain_text(ROOM)
        manager.locator("[data-wr-close]").click()
        wait_room_membership(page, a, b)

        step = "all-room and single-room viewing without switching control room"
        choose_room_view(page, "all")
        expect(page.locator(".wr-trigger")).to_contain_text("所有房间")
        assert_online_members(page, OWNER_UID, A_PEER, B_PEER)
        expect(member(page, OWNER_UID).locator(".wr-member-source")).to_have_attribute("title", a)
        expect(member(page, B_PEER).locator(".wr-member-source")).to_have_attribute("title", b)
        expect(member(page, A_PEER).locator("[data-ui=select]")).to_have_count(1)
        expect(member(page, B_PEER).locator("[data-ui=select]")).to_have_count(0)
        choose_room_view(page, "room", BOOK_B)
        expect(page.locator(".wr-trigger")).to_contain_text(BOOK_B)
        assert_online_members(page, B_PEER)
        expect(member(page, OWNER_UID)).to_have_count(0)
        expect(page.locator("#selectAll")).to_be_hidden()
        wait_room_membership(page, a, b)
        choose_room_view(page, "current")
        assert_online_members(page, OWNER_UID, A_PEER)

        step = "authorize the actual executor via owner UI"
        choose_room_view(page, "manage")
        page.locator(".wr-manager[open] [data-wr-owner]").click()
        selector = owner.locator("[data-owner-target]")
        candidate = selector.locator("option").filter(has_text="隔离网页执行端")
        expect(candidate).to_have_count(1)
        target_key = candidate.get_attribute("value")
        uid, instance, machine = json.loads(target_key)
        assert uid == OWNER_UID and instance == INSTANCE and machine, "Executor identity not confirmed"
        selector.select_option(value=target_key)
        expect(owner.locator("[data-owner-code]")).to_be_visible()
        expect(owner.locator("[data-profile]")).to_have_count(0)
        owner.locator("[data-owner-code]").fill(args["ownerCode"])
        owner.locator("[data-owner-authorize]").click()
        expect(owner.locator("[data-profile]")).to_have_count(2)

        step = "save only B's independent assistant profile while on A"
        item_a = owner.locator(f'[data-profile="{PROFILE_A}"]')
        item_b = owner.locator(f'[data-profile="{PROFILE_B}"]')
        expect(item_a).to_contain_text(a)
        expect(item_a).to_contain_text("当前登录")
        expect(item_b).to_contain_text(b)
        expect(item_b).not_to_contain_text("当前登录")
        item_b.click()
        form = owner.locator(".room-owner-form")
        expect(form.locator('[name="serverUrl"]')).to_have_value(b)
        expect(form.locator('[name="roomCode"]')).to_have_value(ROOM)
        expect(form.locator('[name="expectedPlayers"]')).to_have_value("3")
        expect(form.locator('[name="password"]')).to_have_value("")
        form.locator('[name="note"]').fill(NOTE)
        save_profile = form.get_by_role("button", name="保存档案", exact=True)
        save_profile.click()
        expect(owner.locator("[data-owner-status]")).to_contain_text("房间档案已保存")
        expect(save_profile).to_be_enabled()
        item_a.click()
        expect(form.locator('[name="note"]')).to_have_value("")
        item_b.click()
        expect(form.locator('[name="note"]')).to_have_value(NOTE)
        wait_room_membership(page, a, b)

        step = "edit B's orchestration and explicit standby completion target from A"
        owner.locator("[data-owner-mode]").click()
        a_card = owner.locator(f'.ro-card:has([data-ro-edit="{PROFILE_A}"])')
        b_card = owner.locator(f'.ro-card:has([data-ro-edit="{PROFILE_B}"])')
        expect(owner.locator(".ro-card")).to_have_count(2)
        expect(a_card.locator(".ro-current-label")).to_have_text("当前登录")
        expect(b_card.locator(".ro-current-label")).to_have_count(0)
        expect(a_card).to_contain_text(a)
        expect(b_card).to_contain_text(b)
        expect(a_card).to_contain_text("甲房绑定")
        expect(b_card).to_contain_text("乙房绑定")
        expect(a_card).to_contain_text("2 人")
        expect(b_card).to_contain_text("3 人")
        b_card.locator(f'[data-ro-edit="{PROFILE_B}"]').click()
        editor = owner.locator(".ro-editor[open]")
        expect(editor).to_contain_text(b)
        people = editor.locator('[data-ro-field="people"]')
        expect(people).to_have_value("3")
        expect(editor.locator('[data-ro-field="schedule"]')).to_have_value("")
        save_config = editor.get_by_role("button", name="保存配置", exact=True)
        expect(save_config).to_be_enabled()  # passive catalog read; never start/refresh BGI
        people.fill("4")
        editor.locator('[data-ro-field="action"]').select_option("switchRoom")
        target_room = editor.locator('[data-ro-field="targetRoom"]')
        expect(target_room).to_have_value("")  # explicitly choose, never implicit first room
        option_a = target_room.locator("option").filter(has_text=a)
        expect(option_a).to_have_count(1)
        expect(option_a).to_contain_text("甲房档案")
        expect(option_a).to_contain_text(ROOM)
        target_room.select_option(value=option_a.get_attribute("value"))
        editor.locator('[data-ro-field="afterSwitch"]').select_option("standby")
        save_config.click()
        expect(editor).to_have_count(0, timeout=30000)
        expect(owner.locator(".ro-view-feedback")).to_contain_text("已确认保存并重新读取")
        expect(a_card.locator(".ro-current-label")).to_have_text("当前登录")
        expect(a_card).to_contain_text("2 人")
        expect(b_card).to_contain_text("4 人")
        expect(b_card).to_contain_text("切换至 甲房档案")
        wait_room_membership(page, a, b)

        step = "return to profiles and independently confirm both edits before switching"
        owner.locator("[data-owner-mode]").click()
        expect(item_a).to_contain_text("当前登录")
        item_b.click()
        expect(form.locator('[name="note"]')).to_have_value(NOTE)
        expect(form.locator('[name="expectedPlayers"]')).to_have_value("4")
        switch_button = form.locator("[data-switch]")
        expect(switch_button).to_be_enabled()
        checkpoint("before-switch")

        step = "explicit manual switch of the same executor A to B"
        confirmed = []

        def confirm_switch(dialog):
            confirmed.append(dialog.message)
            if "将此执行端实际切换到所选房间" in dialog.message:
                dialog.accept()
            else:
                dialog.dismiss()

        page.once("dialog", confirm_switch)
        switch_button.click()
        assert len(confirmed) == 1 and "将此执行端实际切换到所选房间" in confirmed[0]
        # This is a read of the actual UI-generated pending receipt, not seeding the owner's storage.
        receipt_expression = """key => {
            const p = JSON.parse(localStorage.getItem('bgi.room.owner.pending.v1') || '{}')[key];
            return p?.operation === 'room.switch.start' ? p.id : '';
        }"""
        page.wait_for_function(receipt_expression, arg=target_key)
        original = page.evaluate(receipt_expression, target_key)
        assert original and original.startswith("room-switch-web-")
        wait_room_membership(page, a, b, switched=True)
        checkpoint("switched", operationId=original)

        step = "query original switch receipt via B without replacing target or replaying request"
        # The owner view rebinds the same identity on the already authenticated secondary room.
        # Only the normal visible receipt check is used; the server fixture verifies exact route and ID.
        expect(owner.locator("[data-owner-target]")).to_have_value(target_key)
        # B can publish membership just before the assistant adopts its prepared connection.
        # After the C# fixture confirms cutover, explicitly use the regular refresh control.
        owner.locator("[data-owner-refresh]").click()
        check = owner.get_by_role("button", name="核对上次操作", exact=True)
        expect(check).to_be_enabled(timeout=25000)
        check.click()
        expect(check).to_have_count(0, timeout=25000)
        expect(item_b).to_contain_text("当前登录")
        expect(item_a).not_to_contain_text("当前登录")
        expect(item_a).to_contain_text("本机房间")
        assert not page.evaluate(receipt_expression, target_key), "Original receipt is still unconfirmed"
        item_b.click()
        expect(form.locator('[name="serverUrl"]')).to_have_value(b)
        expect(form.locator('[name="note"]')).to_have_value(NOTE)
        expect(form.locator('[name="expectedPlayers"]')).to_have_value("4")

        step = "membership and viewing remain independent after the actual switch"
        page.locator("#homeTab").click()
        choose_room_view(page, "all")
        assert_online_members(page, OWNER_UID, A_PEER, B_PEER)
        expect(member(page, OWNER_UID).locator(".wr-member-source")).to_have_attribute("title", b)
        expect(member(page, OWNER_UID).locator("[data-ui=select]")).to_have_count(0)
        expect(member(page, A_PEER).locator(".wr-member-source")).to_have_attribute("title", a)
        choose_room_view(page, "room", BOOK_B)
        assert_online_members(page, OWNER_UID, B_PEER)
        expect(member(page, A_PEER)).to_have_count(0)
        expect(page.locator("#selectAll")).to_be_hidden()
        choose_room_view(page, "current")
        # A retains an offline history card for the switched executor; it is not a current member.
        assert_online_members(page, A_PEER)
        expect(member(page, B_PEER)).to_have_count(0)
        wait_room_membership(page, a, b, switched=True)
        assert page.url.startswith(a + "/")
        assert not errors, "Page script error: " + "; ".join(errors)
        assert not non_fixture_requests, "The page attempted a request outside the fixture origins"
    except Exception as error:
        # No input values, browser credentials, pipe names or raw network frames are logged.
        try:
            detail = page.evaluate("""() => Object.fromEntries([
                ['login', '#loginError'], ['owner', '[data-owner-status]'],
                ['editor', '.ro-editor-message'], ['book', '[data-wr-result]'], ['view', '.wr-trigger-label']
            ].map(([key,selector])=>[key,document.querySelector(selector)?.textContent?.slice(0,300)||'']))""")
        except Exception:
            detail = {}
        raise AssertionError(f"{step}: {error}\nUI: {detail}") from error


def main():
    args = json.loads(sys.stdin.readline())
    args["a"], args["b"] = loopback_origin(args["a"]), loopback_origin(args["b"])
    assert args["a"] != args["b"], "Distinct gateway origins are required"
    edge = os.environ.get("EDGE_PATH") or os.path.join(
        os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), "Microsoft", "Edge", "Application", "msedge.exe")
    if not os.path.isfile(edge):
        raise RuntimeError("Installed Edge not found; set EDGE_PATH to the local executable")
    errors, non_fixture_requests = [], []
    expect.set_options(timeout=15000)
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(executable_path=edge, headless=True, args=[
            "--no-proxy-server", "--disable-background-networking", "--disable-component-update",
            "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1"])
        try:
            context = browser.new_context(viewport={"width": 1440, "height": 900}, service_workers="block", accept_downloads=False)
            context.set_default_timeout(15000)
            context.set_default_navigation_timeout(15000)

            def only_fixture(route):
                url = urlsplit(route.request.url)
                if f"{url.scheme}://{url.netloc}" in (args["a"], args["b"]):
                    route.continue_()
                else:
                    non_fixture_requests.append(url.hostname)
                    route.abort()

            context.route("**/*", only_fixture)
            page = context.new_page()
            page.on("pageerror", lambda error: errors.append(str(error)))
            run(page, args, errors, non_fixture_requests)
            context.close()
        finally:
            browser.close()
    checkpoint("complete")


if __name__ == "__main__":
    try:
        main()
    except Exception as failure:
        print(json.dumps({"stage": "failed", "message": str(failure)[:3000]}, ensure_ascii=False), flush=True)
        sys.exit(1)
