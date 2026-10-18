from pathlib import Path
import html,json,re
ROOT=Path(__file__).resolve().parents[1]
out=ROOT/'web'/'help';out.mkdir(parents=True,exist_ok=True)
groups={}
def add(mode,command,description,example,note=''):
    groups.setdefault(mode,[]).append(dict(command=command,description=description,example=example,note=note))
ks=[
('help','查看模式简介与完整帮助网址','!ks help','直接输入 !ks 效果相同；查看帮助无需训练权限。'),
('practice','进入训练环境','!ks practice','会重新加载地图；等待就绪后选择 T/CT 并出生。'),
('exit','保存当前战术、退出训练并恢复 Dust2 死斗','!ks exit','practice off 是等效退出操作；切换影响全服。'),
('map <地图>','保存当前战术后切换训练地图','!ks map de_mirage','地图必须已安装且是允许的 de_ 地图。'),
('kit [主武器]','补齐护甲、投掷物和训练资金','!ks kit awp','不传主武器时保留已有主武器；支持双闪，也可按 B 自由购买。'),
('new <名称>','在当前地图新建战术，记录阵营和起点','!ks new B区进攻','需 T/CT 存活；同地图重名会拒绝创建，已有战术请 load。'),
('anchor','将当前位置和视角设为新队员起点','!ks anchor','重录已有队员仍使用该队员自己的旧起点；录制保留开始时已购买的装备。'),
('record <1-5>','录制或重录指定队员','!ks record 1','倒计时结束后行动；重录只覆盖该队员，其他轨迹保留；需与战术阵营一致并存活。'),
('stop','结束录制或停止回放','!ks stop','结束录制会保留轨迹；不会退出训练，之后 save 才落盘。控制台 css_ks_stop 可绑定快捷键。'),
('trim <毫秒> [队员]','裁掉轨迹末尾的多余动作','!ks trim 300 2','省略队员时处理最近录制的队员；按 Tick 向上取整，至少保留一帧；修改后 save。'),
('play','同步回放所有已录制队员','!ks play','仅生成有轨迹的 BOT，支持非连续编号；普通 KS 是纯轨迹回放，没有战斗 AI 交接。'),
('replay <1-5>','只回放一个队员','!ks replay 3','支持以观察者身份播放；缺少该轨迹时拒绝。'),
('offset <1-5> <毫秒>','设置该队员相对统一开始时间的延迟','!ks offset 3 1500','范围 0–120000 毫秒；1500 表示晚 1.5 秒开始，之后 save。'),
('remove <1-5>','移除一个队员的轨迹','!ks remove 3','保留其他轨迹；之后 save。'),
('save','将当前战术保存到服务器','!ks save','录制、裁剪、偏移和删除队员后均应主动保存。'),
('load <名称或ID>','加载当前地图共享战术','!ks load B区进攻','支持观察者加载；多人可轮流编辑同一套战术，不是个人隔离库；版本或 Tick 不兼容会拒绝。'),
('list','列出当前地图的战术名称和 ID','!ks list','这里只列出 KS 战术，不包含 KSV 独立场景。'),
('copy <新名称>','将当前战术复制为新 ID 的草稿','!ks copy B区进攻_改版','新名称不可重名；复制后 save 才保存。'),
('delete <名称或ID>','删除当前地图指定战术文件','!ks delete B区进攻_旧版','直接删除共享文件，请确认目标；不会把当前内存草稿自动保存。'),
('reset','停止当前操作并恢复当前战术起点和装备','!ks reset','保留战术；不会恢复已删除文件，也不会退出训练。'),
('cancel','取消当前操作并清理本次 BOT','!ks cancel','游戏内保留战术和训练环境；录制中取消不等于 stop 完成录制。'),
('status','查看环境、状态、战术和最近错误','!ks status','遇到无法录制或回放时先查看状态，地图切换时请等待完成。'),
('reload','重新加载配置和权限','!ks reload','供有相应权限的操作人员使用，不会自动授予白名单权限。'),
('source','显示开源许可与源码地址','!ks source','')]
for a in ks:add('ks','!ks '+a[0],*a[1:])
kv=[
('help','查看 KSV 简介与帮助网址','!ksv help','直接输入 !ksv 效果相同。'),
('practice','进入双阵营模拟训练环境','!ksv practice','需独立 KSV 权限；与 KS、KSD 不能同时占用录制器和 BOT。'),
('exit','保存场景并恢复 Dust2 死斗','!ksv exit','保存失败时不会继续切换。'),
('map <地图>','保存场景后切换训练地图','!ksv map de_inferno','仅支持已安装且允许的 de_ 地图。'),
('new <名称>','新建独立双阵营场景','!ksv new B区3v1','同地图不可重名；成功创建或加载者持有会话，其他人不能抢占。'),
('load <名称或ID>','加载当前地图 KSV 场景','!ksv load B区3v1','与 KS 的 .ks 战术库分开，不能直接加载 KS 文件。'),
('list','列出当前地图 KSV 场景','!ksv list','包含场景 ID、名称和已录制角色数。'),
('anchor t|ct','保存该阵营的新角色起点','!ksv anchor t','必须加入对应阵营且存活；分别设置 T 和 CT 起点。'),
('record t|ct <1-5>','录制或重录指定阵营角色','!ksv record ct 1','需在对应阵营存活；保留当前购买的装备；重录后该角色 handoff 重置为 auto。'),
('stop','完成当前录制，或停止当前模拟','!ksv stop','保留场景与训练环境；控制台快捷停止命令是 css_ksv_stop。'),
('save','保存 KSV 场景及其参数','!ksv save','轨迹、偏移、模式和交接参数编辑后请保存。'),
('remove t|ct <1-5>','移除一个角色及其交接设置','!ksv remove t 3','之后 save；不影响其他角色。'),
('offset t|ct <1-5> <毫秒>','设置该角色开始回放的延迟','!ksv offset ct 1 1500','范围 0–120000 毫秒；延迟和 AI 交接时间分别计算。'),
('mode strict|hybrid','选择严格回放或混合 AI 模式','!ksv mode hybrid','strict：只执行录制，结束后不主动寻敌；hybrid：按每角色交接设置恢复原生战斗 AI。'),
('handoff t|ct <1-5> <毫秒或auto>','设置单个角色的 AI 交接时间','!ksv handoff t 1 auto','仅 hybrid 使用；auto 等待该角色回放自然结束；毫秒从该角色自己的轨迹开始计算，不能超过轨迹长度。'),
('play','让双方 BOT 同步模拟交战','!ksv play','每队需有效轨迹；只生成已录角色，各队 1–5 个；操作者自动转观察者，其他真人须提前转观察者。'),
('repeat on|off','开启或关闭连续模拟','!ksv repeat on','可在模拟中切换；每轮结束清理 BOT 后开始下一轮；save 持久保存场景参数。'),
('status','查看角色、模式、交接、轮次和结果','!ksv status','结果包含胜负、平局、超时或异常取消。'),
('reload','重新加载 KSV 权限与配置','!ksv reload','不继承 KS 或 KSD 白名单。')]
for a in kv:add('ksv','!ksv '+a[0],*a[1:])
kd=[
('help','查看防守模式简介与帮助网址','!ksd help','直接输入 !ksd 效果相同。'),
('practice','进入防守训练环境','!ksd practice','需独立 Defense 权限；就绪后加入 CT，按 B 配装。'),
('exit','结束会话并恢复 Dust2 死斗','!ksd exit','房主执行；切换影响全服。'),
('close','解散防守会话，保留训练环境','!ksd close','房主执行；释放占用后才能开始 KS 或 KSV 操作。'),
('map <地图>','结束会话并切换训练地图','!ksd map de_mirage','房主执行；目标须是已安装且允许的 de_ 地图。'),
('list','列出当前地图已有的 T 方 KS 战术','!ksd list','KSD 不录制进攻轨迹；请先用 KS 在 T 方录制并保存。'),
('load <名称或ID>','只读加载 T 方战术并建立防守会话','!ksd load B区进攻','房主需在 CT；成功后自动加入防守队伍；不会改写原 .ks 文件。'),
('join','加入当前防守队伍','!ksd join','成员需 Defense 权限且在 CT；训练停止时加入；队伍最多 5 人。'),
('leave','离开防守队伍','!ksd leave','普通成员使用；交火中延迟到本轮结束；房主应 stop 或 close，不能直接 leave。'),
('team','查看房主、队员和准备状态','!ksd team','开始前确认每位队员已 join 且已 position。'),
('position','保存自己的防守位置、视角和配装','!ksd position','先加入队伍，在 CT 存活并停在防守点；训练必须停止。每人单独保存。'),
('start','开始防守训练','!ksd start','房主执行；捕获当前装备，统一复活与就位后倒计时；无关真人须转观察者。'),
('stop','停止训练，保留队伍和训练环境','!ksd stop','房主执行；改位置、难度或交接前先 stop。'),
('repeat on|off','设置是否自动开始下一轮','!ksd repeat off','房主可在训练中切换；配置自动保存。'),
('difficulty <1-4>','设置原生 AI 战斗难度','!ksd difficulty 3','房主在停止状态设置；1 简单、2 普通、3 困难、4 专家。'),
('handoff <1-5> <毫秒或auto>','设置某个 T BOT 从录制切换 AI 的时间','!ksd handoff 1 auto','房主在停止状态设置；从该 BOT 自身轨迹开始计时，不能超过轨迹长度；auto 为自然回放结束。'),
('status','查看训练状态、交接和团队成绩','!ksd status','显示成功、失败、平局、超时和存活人数；一名 CT 死亡不终止其他队友防守。'),
('reload','重新加载 Defense 配置与权限','!ksd reload','房主执行；权限被撤销可能移除成员或解散会话。')]
for a in kd:add('ksd','!ksd '+a[0],*a[1:])
for mode in ['ks','ksv']:
    add(mode,f'!{mode} xray on|off','开启或关闭自己的观战 X 光',f'!{mode} xray on','仅对应模式已授权观察者在回放中可见；T/CT 参战玩家不可见。KS 默认关闭，KSV 默认开启。')
    add(mode,f'!{mode} xray colors','查看角色轮廓配色',f'!{mode} xray colors','开关偏好各观察者独立；角色配色是本模式全体观察者共享的设置。')
    add(mode,f'!{mode} xray color '+('<1-5>' if mode=='ks' else 't|ct <1-5>')+' <RRGGBB>','修改角色轮廓颜色',f'!{mode} xray color '+('1' if mode=='ks' else 'ct 1')+' 0088FF','使用六位十六进制 RGB，可带 #；修改即时生效并保存，不必停止回放。')
    add(mode,f'!{mode} xray resetcolors','恢复本模式默认配色',f'!{mode} xray resetcolors','只重置对应模式的共享颜色；KSD 不支持 X 光。')
quick={
'ks':('KS · 录一套战术', [('进入训练，选择 T 或 CT，等出生。','!ks practice'),('按 B 买好装备，站到起点，新建战术。','!ks new 我的战术'),('倒计时结束后行动，完成后停止。','!ks record 1\n!ks stop'),('换编号继续录；保存后即可回放。','!ks save\n!ks play')]),
'ksv':('KSV · 双方 BOT 对打',[('进入训练，选择 T，创建场景。','!ksv practice\n!ksv new 双方演练'),('买好装备并设置 T 起点，录制 T1。','!ksv anchor t\n!ksv record t 1\n!ksv stop'),('切到 CT，买好装备，录制 CT1。','!ksv anchor ct\n!ksv record ct 1\n!ksv stop'),('保存并模拟；其他真人先转观察者。','!ksv mode hybrid\n!ksv save\n!ksv play')]),
'ksd':('KSD · 真人守点',[('先有一套已保存的 T 方 KS 战术；进入训练并选择 CT。','!ksd practice'),('加载进攻战术，买好装备并走到防守点。','!ksd load B区进攻\n!ksd position'),('朋友进 CT 后各自加入并保存位置。','!ksd join\n!ksd position'),('房主确认队伍就绪后开始。','!ksd team\n!ksd start')])}
esc=html.escape
parts=[]
for mode,(title,steps) in quick.items():
    parts.append(f'<article class="quick" id="quick-{mode}"><span class="label">{mode.upper()}</span><h3>{title}</h3><ol>')
    for desc,cmd in steps:parts.append(f'<li><p>{esc(desc)}</p><pre><code>{esc(cmd)}</code></pre></li>')
    parts.append('</ol></article>')
tables=[]
for mode,rows in groups.items():
    tables.append(f'<section class="command-section" id="{mode}" data-mode="{mode}"><h3>{mode.upper()} 指令大全 <small>{len(rows)} 项</small></h3><div class="table-wrap"><table><thead><tr><th>指令 / 示例</th><th>用途与使用条件</th></tr></thead><tbody>')
    for r in rows:
        tables.append(f'<tr data-search="{esc(mode+" "+" ".join(r.values()),quote=True)}"><td><code class="syntax">{esc(r["command"])}</code><div class="example"><code>{esc(r["example"])}</code><button type="button" data-copy="{esc(r["example"],quote=True)}" aria-label="复制示例 {esc(r["example"],quote=True)}">复制</button></div></td><td><strong>{esc(r["description"])}</strong><p>{esc(r["note"])}</p></td></tr>')
    tables.append('</tbody></table></div></section>')
page='''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="description" content="KeranStrat KS、KSV、KSD 快速上手和完整指令手册，包含录制、回放、双阵营模拟、协同防守与 X 光观察。"><title>KeranStrat 使用手册 · KS / KSV / KSD</title><style>
:root{color-scheme:dark;--bg:#0a0d12;--panel:#111821;--line:#263343;--text:#e9eef5;--muted:#a7b5c5;--accent:#70bcff}*{box-sizing:border-box}html{scroll-behavior:smooth;scroll-padding-top:24px}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.75 system-ui,-apple-system,"Segoe UI","Microsoft YaHei",sans-serif}a{color:var(--accent);text-underline-offset:4px}header{border-bottom:1px solid var(--line)}.bar,main,footer{max-width:1200px;margin:auto;padding:20px 28px}.bar{display:flex;justify-content:space-between;gap:20px;align-items:center}.brand{font-weight:800;letter-spacing:1px}.bar nav{display:flex;gap:18px;flex-wrap:wrap}.hero{padding:48px 0 28px}.eyebrow,.label{font-size:12px;letter-spacing:2px;color:var(--accent);font-weight:700}h1{font-size:clamp(30px,5vw,54px);line-height:1.2;margin:14px 0}h2{font-size:26px;margin:36px 0 16px}h3{font-size:20px;margin:12px 0}p{margin:8px 0;color:var(--muted)}.intro{font-size:17px;max-width:760px}.modes,.quick-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.mode,.quick,.note,details{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:22px}.mode strong{font-size:18px}.mode p{font-size:14px}.mode a{display:inline-block;margin-top:8px}.note{margin:20px 0;border-left:3px solid var(--accent);padding:16px 20px}.quick ol{padding-left:22px;margin-bottom:0}.quick li{padding-left:4px;margin-bottom:18px}.quick li:last-child{margin-bottom:0}code{font:13px/1.65 ui-monospace,SFMono-Regular,Consolas,monospace;color:#b9dcff;overflow-wrap:anywhere}pre{background:#090e15;border:1px solid var(--line);padding:10px 12px;border-radius:6px;overflow-x:auto;white-space:pre-wrap;margin:8px 0}.controls{display:flex;gap:12px;flex-wrap:wrap;align-items:center;background:var(--panel);padding:16px;border:1px solid var(--line);border-radius:10px}.search{flex:1;min-width:220px}.search input{width:100%;background:var(--bg);border:1px solid #40566e;border-radius:6px;padding:12px;color:var(--text);font:inherit}button,.filter{border:1px solid #40566e;background:#172536;color:var(--text);border-radius:6px;padding:8px 13px;cursor:pointer;font:inherit}.filters{display:flex;gap:8px;flex-wrap:wrap}.filter[aria-pressed=true]{background:#24496b;border-color:var(--accent)}button:hover{border-color:var(--accent)}button:focus-visible,a:focus-visible,input:focus-visible,summary:focus-visible{outline:2px solid var(--accent);outline-offset:3px}.table-wrap{overflow-x:auto;border:1px solid var(--line);border-radius:10px}table{width:100%;border-collapse:collapse;text-align:left}th{background:#172231;color:var(--muted);font-size:13px}td,th{padding:17px 20px;vertical-align:top;border-bottom:1px solid var(--line)}tr:last-child td{border-bottom:0}td:first-child{width:44%;min-width:220px}td strong{font-weight:600}td p{font-size:14px}.syntax{font-weight:700;font-size:14px}.example{display:flex;gap:10px;align-items:center;margin-top:10px;justify-content:space-between}.example button{font-size:12px;padding:3px 9px;flex-shrink:0}.command-section{margin-top:28px}.command-section h3 small{font-size:12px;color:var(--muted);font-weight:400}details{margin:10px 0;padding:14px 20px}summary{cursor:pointer;font-weight:600}details p{margin-top:12px}.end{margin:28px 0}.muted,footer{color:var(--muted);font-size:13px}[hidden]{display:none!important}footer{border-top:1px solid var(--line);margin-top:32px;padding-bottom:32px}#result{min-height:24px;margin-top:10px}#copy-status{position:fixed;bottom:24px;left:50%;transform:translateX(-50%);background:#1e3a51;border:1px solid #70bcff;padding:9px 18px;border-radius:8px;pointer-events:none}#copy-status:empty{display:none}@media(max-width:850px){.quick-grid,.modes{grid-template-columns:1fr}.bar{align-items:flex-start}.bar,main,footer{padding-left:18px;padding-right:18px}.hero{padding-top:32px}td,th{padding:13px 12px}.controls{padding:12px}.bar nav{gap:10px;font-size:13px}}@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}}
</style></head><body><header><div class="bar"><a class="brand" href="/">KERANSTRAT</a><nav aria-label="页面导航"><a href="#quick-start">快速上手</a><a href="#commands">指令大全</a><a href="#faq">常见问题</a></nav></div></header><main><section class="hero"><div class="eyebrow">CS2 TACTICAL TRAINING / 使用手册</div><h1>录下战术，开始演练。</h1><p class="intro">第一次用？照着快速上手操作。想查具体参数？在指令大全里搜索。所有 <code>!指令</code> 均在游戏聊天框输入。</p></section><div class="modes"><div class="mode"><strong>KS · 录制与回放</strong><p>一个人分角色录制团队战术，再让 BOT 同步重演。纯回放，不交接战斗 AI。</p><a href="#quick-ks">开始录制 →</a></div><div class="mode"><strong>KSV · 双阵营模拟</strong><p>分别录制 T、CT，让双方 BOT 对打；你作为观察者观看。</p><a href="#quick-ksv">开始模拟 →</a></div><div class="mode"><strong>KSD · 真人协同防守</strong><p>真人 CT 对抗已有 T 方战术 BOT；录制结束后 BOT 可接管 AI 继续进攻。</p><a href="#quick-ksd">开始守点 →</a></div></div><aside class="note">训练操作需要对应模式的白名单权限，三套权限独立。KS、KSV、KSD 共用服务器，同一时间只能运行一种；进入训练、切图和退出可能影响全服。<strong>查看帮助无需权限。</strong></aside><h2 id="quick-start">快速上手</h2><p>示例名称可自行替换。每次发出录制指令后，等倒计时结束再行动；完成动作后发送 stop。</p><div class="quick-grid">QUICK</div><aside class="note">结束本次训练：KS 用 <code>!ks exit</code>，KSV 用 <code>!ksv exit</code>，KSD 房主用 <code>!ksd exit</code>，恢复 Dust2 死斗。<code>stop</code> 只停止当前操作，不退出训练。</aside><h2 id="commands">指令大全</h2><p><code>&lt;参数&gt;</code> 必填，<code>[参数]</code> 可选，<code>t|ct</code> 表示二选一，不要输入尖括号或竖线。1000 毫秒 = 1 秒。队员编号为 1–5（受服务器配置上限约束），可以只录 1、3、5。</p><div class="controls" id="controls" hidden><label class="search"><span class="muted">搜索指令、用途或参数</span><input id="search" type="search" placeholder="例如：record、交接、X 光、保存" autocomplete="off"></label><div class="filters" role="group" aria-label="筛选模式"><button class="filter" data-filter="all" aria-pressed="true">全部</button><button class="filter" data-filter="ks" aria-pressed="false">KS</button><button class="filter" data-filter="ksv" aria-pressed="false">KSV</button><button class="filter" data-filter="ksd" aria-pressed="false">KSD</button></div></div><p id="result" class="muted" role="status" aria-live="polite"></p>TABLES<h2>快捷停止录制</h2><p>在 <strong>客户端控制台</strong> 绑定一次，不在聊天框输入。会替换鼠标中键原功能；KS 与 KSV 绑定不同，按当前模式选一条，也可以换用其他键。</p><pre><code>bind "mouse3" "css_ks_stop"     // KS\nbind "mouse3" "css_ksv_stop"    // KSV</code></pre><h2 id="faq">常见问题</h2><details><summary>我想用 AWP 加双闪录制，怎么配装？</summary><p>进入训练并出生后按 B 购买。KS 也可用 <code>!ks kit awp</code>。录制保留当时已有的武器与投掷物；不用担心回到起点时被强制改成 AK。KSV、KSD 直接按 B 配装；KSD 买好后保存 position，改装后重新 start。</p></details><details><summary>为什么打开 X 光还是看不到？</summary><p>仅 KS、KSV 回放中，对应模式已授权的观察者才能看到。先切到观察者，再 <code>!ks load 名称</code> → <code>!ks xray on</code> → <code>!ks play</code>。KSV 操作者 play 后自动转观察者。观察者没有存活角色，不能录制；回到对应 T/CT 后再录制。KSD 不支持 X 光。</p></details><details><summary>AI 交接与 offset 有什么区别？</summary><p>offset 是角色晚多久开始；handoff 是开始后多久从录制转 AI。例如 offset 1500、handoff 3000 表示统一起点后 1.5 秒开始，该角色播放 3 秒后交接。交接不能超过轨迹长度，不确定就用 auto。普通 KS 不支持交接；KSV 必须使用 hybrid。</p></details><details><summary>为什么提示模式占用或不能编辑？</summary><p>先停止正在运行的录制或回放。KSV 由当前操作者使用 <code>!ksv exit</code> 释放；KSD 房主可用 <code>!ksd close</code> 解散会话并保留训练环境。KSD 普通成员只能管理自己的加入、退出和位置。</p></details><details><summary>KSD 队友死了会立刻复活吗？</summary><p>不会。本轮死亡后等待，其他队友继续防守。下一轮由防守回合管理器统一复活和恢复各自位置装备；普通训练的自动复活不适用于 KSD。</p></details><details><summary>保存的战术是我独享的吗？</summary><p>KS 按地图共享战术库，有权限的玩家可轮流 load 同一套战术编辑。新建时同地图名称不能重复。KSV 使用独立共享场景库和会话操作权，与 KS 文件不互通。KSD 只读引用 KS 的 T 方战术，防守位置按成员独立保存。</p></details><details><summary>为什么不能加载，或者提示没有权限？</summary><p>三种模式白名单独立，请联系管理员核对。战术按地图存放，先确认当前地图；游戏更新导致录制版本或 Tick 不兼容时，需重新录制。用对应模式的 status 查看具体错误。</p></details><p class="end">需要暂停：<code>!ks stop</code> / <code>!ksv stop</code> / <code>!ksd stop</code>。需要退出：使用当前模式的 <code>exit</code>。</p></main><footer>KeranStrat 0.5.5 · 指令按当前源码核对 · <a href="https://github.com/HifumiDaisuki01/KeranStrat">GitHub 源码</a> · <a href="/">返回服务器首页</a></footer><div id="copy-status" role="status" aria-live="polite"></div><script>
document.getElementById('controls').hidden=false;let mode='all';const input=document.getElementById('search'),sections=[...document.querySelectorAll('.command-section')];function filter(){const q=input.value.trim().toLowerCase();let n=0;for(const s of sections){let shown=0;for(const r of s.querySelectorAll('tbody tr')){const yes=(mode==='all'||mode===s.dataset.mode)&&r.dataset.search.toLowerCase().includes(q);r.hidden=!yes;if(yes)shown++}s.hidden=!shown;n+=shown}document.getElementById('result').textContent=n?'显示 '+n+' 项指令':'没有匹配项，请换个关键词或切换到全部模式。'}input.addEventListener('input',filter);document.querySelectorAll('[data-filter]').forEach(b=>b.addEventListener('click',()=>{mode=b.dataset.filter;document.querySelectorAll('[data-filter]').forEach(x=>x.setAttribute('aria-pressed',String(x===b)));filter()}));document.querySelectorAll('[data-copy]').forEach(b=>b.addEventListener('click',async()=>{let message;try{await navigator.clipboard.writeText(b.dataset.copy);message='示例已复制'}catch{message='请选中示例文字，手动复制'}const s=document.getElementById('copy-status');s.textContent=message;setTimeout(()=>{s.textContent=''},2300)}));filter();
</script></body></html>'''
page=page.replace('QUICK',''.join(parts)).replace('TABLES',''.join(tables))
(out/'index.html').write_text(page,encoding='utf-8')
(out/'commands.json').write_text(json.dumps(groups,ensure_ascii=False,indent=2),encoding='utf-8')
print('Generated',sum(map(len,groups.values())),'commands;',len(page.encode()),'HTML bytes')
