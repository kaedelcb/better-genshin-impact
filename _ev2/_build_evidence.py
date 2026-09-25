import hashlib, os, zipfile
import xml.etree.ElementTree as ET

NS = {'m': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
TRX_DIR = r'Test/BetterGenshinImpact.UnitTest/TestResults'

# 帧清单与身份标签：显式映射（第 3 轮必改4 处置——不再用子串匹配推断，杜绝 green4/final3 被误标）
# （帧, 身份标签）
FRAMES = [
    ('_ev2_targeted_green.trx',  '定向绿（初版夹具形态）'),
    ('_ev2_targeted_green2.trx', '定向绿（第1轮突变还原后）'),
    ('_ev2_targeted_green3.trx', '定向绿（第1轮加固后）'),
    ('_ev2_targeted_green4.trx', '定向绿（第2轮修复后）'),
    ('_ev2_targeted_green5.trx', '定向绿（第3轮修复后）'),
    ('_ev2_targeted_green6.trx', '定向绿（第5轮修复后终帧）'),
    ('_ev2_mutM1_red.trx',       '突变红 M1（第5轮修复后形态重做）'),
    ('_ev2_mutM2_red.trx',       '突变红 M2（第5轮修复后形态重做）'),
    ('_ev2_mutM3_red.trx',       '突变红 M3（第5轮修复后形态重做）'),
    ('_ev2_mutM4_red.trx',       '突变红 M4（第5轮修复后形态重做）'),
    ('_ev2_mutM5_red.trx',       '突变红 M5（第5轮修复后形态重做）'),
    ('_ev2_mutM6_red.trx',       '突变红 M6（方向反转；第5轮修复后形态重做）'),
    ('_ev2_mutM7_red.trx',       '突变红 M7（队列满注册表写入拆除，第5轮新增）'),
    ('_ev2_mutM6_jobregistry_red.trx', '佐证帧：M6 下 JobRegistryTests 先终态者赢合同夹具实跑红（第5轮建议4）'),
    ('_ev2_bgi_full_final2.trx', '全量回归（第1轮加固后；被最新帧取代留档）'),
    ('_ev2_bgi_full_final3.trx', '全量回归（第2轮修复后；被最新帧取代留档）'),
    ('_ev2_bgi_full_final4.trx', '全量回归（第3/4轮形态；被最新帧取代留档）'),
    ('_ev2_bgi_full_final5.trx', '全量回归（最终帧：差集=基线空集）'),
]

def counters(path):
    c = ET.parse(path).find('.//m:Counters', NS)
    a = c.attrib if c is not None else {}
    return f"{a.get('passed','?')}/{a.get('failed','?')}/{a.get('total','?')}"

lines = [f'# ev2 TRX 摘录（{len(FRAMES)} 帧；解析逻辑：TRX XML Counters＋UnitTestResult@outcome）', '']
lines.append('| 帧 | 通过/失败/总数 | 身份 |')
lines.append('|---|---|---|')
for f, note in FRAMES:
    p = os.path.join(TRX_DIR, f)
    lines.append(f"| {f} | {counters(p)} | {note} |")
lines.append('')
lines.append('## 突变红帧失败名单（守护断言归因）')
for f, _ in FRAMES:
    if 'mutM' not in f:
        continue
    t = ET.parse(os.path.join(TRX_DIR, f))
    lines.append(f"\n### {f}")
    for r in t.findall('.//m:UnitTestResult', NS):
        if r.get('outcome') == 'Failed':
            # 消息优先取 ErrorInfo/Message（xUnit 失败消息实际位置），回退 Output/Message。
            # 注意：ElementTree 的 Element 真值＝子元素数，含纯文本的 <Message> 为 falsy，
            # 必须用 is None 判定（第 6 轮必改1：`or` 短路在纯文本元素上永远不生效）。
            node = r.find('m:Output/m:ErrorInfo/m:Message', NS)
            if node is None:
                node = r.find('m:Output/m:Message', NS)
            msg = (node.text or '').strip().replace('\r', '').replace('\n', ' ')[:120] if node is not None else '(未解析到消息)'
            lines.append(f"- {r.get('testName')} — {msg}")

with open('_ev2/ev2_trx_excerpts.md', 'w', encoding='utf-8') as fh:
    fh.write('\n'.join(lines) + '\n')

sha_lines = [f'# ev2 TRX SHA-256 完整性清单（{len(FRAMES)} 帧，权威帧数以本清单为准）', '',
             f'本批 {len(FRAMES)} 帧全部打包随批入库（_ev2/ev2_trx_archive.zip）；松散帧留在 TestResults/ 未跟踪。', '',
             '| 文件 | 字节数 | SHA-256 |', '|---|---|---|']
with zipfile.ZipFile('_ev2/ev2_trx_archive.zip', 'w', zipfile.ZIP_DEFLATED) as z:
    for f, _ in FRAMES:
        p = os.path.join(TRX_DIR, f)
        data = open(p, 'rb').read()
        sha_lines.append(f"| {f} | {len(data)} | {hashlib.sha256(data).hexdigest()} |")
        z.write(p, f)

# zip 自锚（第 9 轮建议 4）＋逐行解压比对记录入仓（第 10 轮建议 3）
zip_data = open('_ev2/ev2_trx_archive.zip', 'rb').read()
sha_lines += ['',
              f'- ev2_trx_archive.zip 自锚：字节数 {len(zip_data)}、SHA-256 `{hashlib.sha256(zip_data).hexdigest()}`',
              '', '## 逐行解压比对（zip 内条目 vs 上表，生成时机械执行）']
zv = zipfile.ZipFile('_ev2/ev2_trx_archive.zip')
match_cnt = 0
for f, _ in FRAMES:
    data = zv.read(f)
    ok = hashlib.sha256(data).hexdigest() == hashlib.sha256(open(os.path.join(TRX_DIR, f), 'rb').read()).hexdigest()
    match_cnt += ok
    sha_lines.append(f'- {f}：{"一致" if ok else "不一致!"}')
sha_lines.append(f'- 比对结果：{match_cnt}/{len(FRAMES)} 一致（与台账 evidence_self_verification 同口径）。')
with open('_ev2/ev2_trx_sha256.md', 'w', encoding='utf-8') as fh:
    fh.write('\n'.join(sha_lines) + '\n')

print('excerpts + sha + zip ok:', os.path.getsize('_ev2/ev2_trx_archive.zip'), 'bytes')
