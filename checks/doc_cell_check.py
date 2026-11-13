import io, re, sys

# 输出里有中文判据名，Windows 默认 GBK 控制台会把读数写成乱码字节，先固定成 UTF-8。
sys.stdout.reconfigure(encoding='utf-8')

# 校验：文档里用 `组/格子名` 形式引用的判据，必须能在 Battery.Add 的注册里逐字找到。
# 做法是只取注册行里的字面量，不用任何模糊匹配——文档写错一个字就该报出来。
reg = []
for path in [
    'checks/LongStitch.Checks/BlitTests.cs', 'checks/LongStitch.Checks/PngTests.cs',
    'checks/LongStitch.Checks/LayoutTests.cs', 'checks/LongStitch.Checks/LongScrollTests.cs',
    'checks/LongStitch.Checks/CanvasInteractionTests.cs',
    'checks/LongStitch.Checks/SeamCropTests.cs',
    'checks/LongStitch.Checks/CropBandDragTests.cs',
    # 第十四轮新增的两个电池文件必须在这里登记。漏登记不会报错，只会让这两格里注册的判据
    # **不进 reg**——于是文档里引用它们全被判成"查无出处"，而源码注释里的 <c>…</c> 也静默不参与校验。
    'checks/LongStitch.Checks/HudCostTests.cs',
    'checks/LongStitch.Checks/SessionDiagnosticsTests.cs',
    'checks/LongStitch.Checks/MatchingTests.cs', 'checks/LongStitch.Checks/CleanupTests.cs',
    'checks/LongStitch.Checks/ExportTests.cs', 'checks/LongStitch.Checks/UiSmokeTests.cs',
    # 第四十八改新增：白天／黑夜／跟随系统那一组。漏登记不会报错，只会让产品注释里
    # 那三枚 <c>ui/…</c> 引用被判成"查无出处"（本批开跑前实测就是这三条 MISSING）。
    'checks/LongStitch.Checks/ThemeTests.cs',
    'checks/LongStitch.Checks/GuardTests.cs',
]:
    text = io.open(path, encoding='utf-8-sig').read()
    for m in re.finditer(r'Battery\.(?:Add|Skip)\("([a-z]+)",\s*"([^"]+)"', text):
        reg.append(m.group(1) + '/' + m.group(2))

docs = ['README.md', 'docs/精度红线.md', 'docs/架构说明.md']
# 源码注释里也引用判据名（第八轮漏过一处：注释写了一个根本不存在的 ui/… 格子）。
# 约定：代码注释里引用判据必须写成 <c>组/格子名</c>，且逐字对齐注册行——
# 用尖括号包起来是因为散文式引用（`判据 png/xxx 把它抓了出来`）没法机器判断名字到哪里结束。
# 第九轮新增组名 crop（裁切拼缝）：加组要同时改下面两条交替式，漏一条就是"引用查不到"或"名字漂移"。
import os
for root, dirs, files in os.walk('src'):
    dirs[:] = [d for d in dirs if d not in ('bin', 'obj')]        # 构建产物里的 LongStitch.xml 不算引用
    docs += [os.path.join(root, f).replace('\\', '/') for f in files if f.endswith('.cs')]
# 第十二轮新增组名 align（公共坐标 + 一条蓝线）：加组必须同时改这条交替式，
# 漏了不会报错，只会让文档里那一组引用**静默不参与校验**——名字漂移就查不出来了。
GROUPS = r'(?:blit|png|layout|longscroll|canvas|crop|align|match|cleanup|export|ui|guard)'
pat = re.compile(r'((?:' + GROUPS + r')/[^`\n，、）)]+)')
code_pat = re.compile(r'<c>((?:' + GROUPS + r')/[^<\n]+)</c>')

refs = []
for d in docs:
    text = io.open(d, encoding='utf-8-sig').read()
    if d.endswith('.cs'):
        for m in code_pat.finditer(text):
            refs.append((d, m.group(1)))
    else:
        for m in pat.finditer(text):
            refs.append((d, m.group(1)))

print('注册格子数：', len(reg))
bad = 0
for d, r in refs:
    norm = r.rstrip('（(').strip()
    # 散文里写「guard 这一整组」会写成 `guard/*`——它指组不指格子，本来就取不到逐字格名。
    # 但不能因此放过：组名打错（`gurad/*`）同样是呆账。所以通配只允许一种核对方式：
    # 该组必须真有已注册的格子，一个都没有就照旧 MISSING。
    if norm.endswith('/*'):
        grp = norm[:-2] + '/'
        if any(c.startswith(grp) for c in reg):
            continue
    # 代码注释里的引用不留缩写余地：必须与注册名一字不差。
    hits = [c for c in reg if c == norm] if d.endswith('.cs') else [c for c in reg if c == norm or c.startswith(norm)]
    if not hits:
        bad += 1
        print('MISSING  <- %s 引用：%s' % (d, norm))
    elif len(hits) > 1 and norm not in reg:
        print('AMBIG    %s 引用：%s 命中 %s' % (d, norm, hits))
print('文档引用总数：', len(refs), ' 找不到出处的：', bad)
sys.exit(1 if bad else 0)
