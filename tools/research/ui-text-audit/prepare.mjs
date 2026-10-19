import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';

const hash = s => crypto.createHash('sha256').update(s).digest('hex');
export function batch(c) {
  const s = `${c.path} ${c.symbol} ${c.text} ${c.role}`;
  if (/DeviceManager/.test(c.path)) return '03 设备管理';
  if (/Calibration|FitTest|HearingSetup/.test(c.path)) return '04 校准窗口';
  if (/TrayApplication/.test(c.path)) return '05 托盘';
  if (/Settings|Resources|Resource/.test(c.path)) return '03 应用设置与资源';
  if (/Anc|Equalizer|Eq|Spatial|HiRes|Game|Gesture|Feature|Daily/.test(s)) return '02 耳机功能';
  if (/Main|Selection|Device|ClientBridge|Backend|Worker/.test(c.path)) return '01 主界面与连接';
  return '05 辅助与异常';
}

// These are named, source-checkable execution paths. Individual indirect branches remain explicit gaps.
const routes = [
  { id:'EQ-SAVE', test:/SaveEq|SaveEqualizer|SaveCore|保存并启用|保存并选用|保存.*EQ/, label:'保存并激活自定义 EQ', scene:'MAIN-EQ',
    nodes:['SaveEqAsync','ApplyWithResultAsync','ExecuteIntentAsync','ApplyAsync','SaveEqualizerAsync','SaveCoreAsync','SendAndObserveAsync'],
    chain:'SaveEqCommand → MainViewModel.SaveEqAsync → UiAction(CustomEqualizer) → ClientBridge.ApplyAsync → 后台 apply 路由 → NativeHeadsetService.ApplyAsync → DailyClient.SaveEqualizerAsync → SaveCoreAsync → SendAndObserveAsync(0x0418)',
    behavior:'校验名称与草稿，保存且激活 EQ；依据设备响应及设备回读分别报告结果。不会因未确认响应而擅自重发。',
    limits:'CanSetCustomEqualizer && EqDirty；还受连接、已读条目、名称/容量/增益校验约束。' },
  { id:'EQ-EDIT', test:/Eq|Equalizer|Preset|均衡|预设/, label:'EQ 编辑、选择与状态', scene:'MAIN-EQ',
    nodes:['LoadEntry','UseEqAsync','ApplyPresetAsync','SelectCustomEqualizerAsync','SendAndObserveAsync'],
    chain:'AXAML EQ 区域／EqualizerChoiceView → MainViewModel 的 EQ 属性、命令 → UiAction(Equalizer/CustomSelect/CustomEqualizer) → ClientBridge → Host → DailyClient 对应执行方法',
    behavior:'恢复当前只回填编辑器；选择预设、选用条目、保存条目是不同操作。文字应区分草稿、已提交、回读确认。', limits:'编辑与保存门控不同；逐条以具体命令及源码条件为准。' },
  { id:'ANC', test:/Anc|降噪|通透|噪声|档位/, label:'降噪模式与子档位', scene:'MAIN-ANC',
    nodes:['ApplyAncModeAsync','ApplyAncAsync','ApplyAsync'],
    chain:'AncModeSelector／MainViewModel.Anc → AncMode 或 Anc 操作 → ClientBridge.ApplyAsync → NativeHeadsetService.ApplyAsync → DailyClient.ApplyAncModeAsync / ApplyAncAsync',
    behavior:'主模式与子档位分开提交；未知子状态保持未确认。通知回填和绑定更新只展示，不发送设置。', limits:'取决于欢律配置、协议能力及当前已知状态；固件最低版本提示不单独禁用。' },
  { id:'FEATURE', test:/FeatureControl|FeatureSection|BuildFeature|Gesture|游戏|双设备|入耳|人声|低音|空间|HiRes|Hi-Res|低延迟|长续航|提示音|长按|来电|睡眠/, label:'配置驱动的耳机功能', scene:'MAIN-FEATURE',
    nodes:['SubmitAsync','ApplyFeatureAsync','SetSpatialModeAsync','SetHiResAsync','SetGameModeAsync','SetDualConnectionAsync'],
    chain:'DailyClient.BuildFeatureSections → UiFeature 定义 → FeatureControlView / FeatureControlViewModel → SubmitAsync → UiAction(Feature, FeatureId) → ClientBridge → NativeHeadsetService.ApplyAsync → DailyClient.ApplyFeatureAsync 或双设备/游戏/空间/HiRes专用方法',
    behavior:'开关、下拉、滑块、文本和手势按显式输入提交。选项显示顺序、协议编号与型号条件分别处理。', limits:'型号/选项全集由配置与已实现 codec 决定；一个演示型号截图不能证明全部型号。' },
  { id:'CALIBRATION', test:/Calibration|Hearing|FitTest|HearingSetup|贴合|听感|测试|试听|响度|扫描|左耳.*右耳/, label:'贴合度与黄金听感流程', scene:'CAL-READY',
    nodes:['SendAsync','CancelAsync','PrepareAsync','InputAsync'],
    chain:'CalibrationView 按钮／滑块 → CalibrationViewModel.SendAsync / CancelAsync → ICalibrationService（ClientBridge） → Host 校准路由 → CalibrationEngine 与设备传输；试听走 Host 的试听播放器',
    behavior:'按 kind、phase、Stage 和设备连接代次展示；开始、回答、保存、试听和停止具有不同操作。停止失败不能展示为设备已停止。', limits:'Ready/扫描/回答/Results/Completed/Failed/Cancelled/Disconnected 等分支分别核对；无真机验证。' },
  { id:'MANAGER', test:/DeviceManager|DeviceCandidate|Whitelist|白名单|耳机列表|设备列表/, label:'管理允许控制的已配对耳机', scene:'MANAGER',
    nodes:['RefreshAsync','SaveAsync','EnumerateCandidatesAsync','SaveWhitelistAsync'],
    chain:'DeviceManagerView → DeviceManagerViewModel.RefreshAsync / SaveAsync → IDeviceManagementService（ClientBridge） → Host 设备目录/白名单路由；打开蓝牙设置走系统 URI',
    behavior:'复选框暂存选择；保存选中设备标识，成功后关闭。刷新不等于写入白名单；取消保留旧配置。', limits:'至少选中一台、非忙碌且未退出；后端失败时保留错误并允许显式重试。' },
  { id:'RESOURCE', test:/Resource|资源|云端|密钥|下载/, label:'资源设置、下载与状态', scene:'SETTINGS',
    nodes:['SaveResourceSettingsAsync','RedownloadResourcesAsync','SetOnlineEnabledAsync'],
    chain:'AppSettingsPanel → MainViewModel.Resources / ClientBridge.Resources → Host.BackendSupervisor.Resources → ResourceCoordinator / VendorResourceClient / ResourceCache',
    behavior:'控制资源来源偏好与显式重新下载；关闭在线资源后仍可使用较新的已缓存资源。资源存在不授予耳机控制权限。', limits:'本次仅固定内置配置，未读取用户密钥和本机缓存；在线增量未闭合。' },
  { id:'APPEARANCE', test:/AppSettings|Theme|FrameRate|Gpu|GPU|主题|帧率|软件渲染|跟随显示器/, label:'应用外观与模型渲染偏好', scene:'SETTINGS',
    nodes:['ApplyTheme','Save','ModelGpuChanged','ModelFrameRateChanged'],
    chain:'AppSettingsPanel 控件事件 → ThemePreferences / ModelRenderingPreferences → 应用主题或模型渲染循环；本地偏好持久化',
    behavior:'修改应用主题、模型 GPU 和帧率偏好；属于本机界面设置。', limits:'具体事件与持久化方法见条目源上下文；不涉及耳机 SET。' },
  { id:'TRAY', test:/TrayApplication|托盘/, label:'后台托盘', scene:'TRAY',
    nodes:['ToolTipFor','ActivateMain','RequestExitAsync'],
    chain:'TrayApplication：打开主界面 → BackendPipeServer.ActivateMain；退出 → RequestExitAsync；设备通知 → ShowDevices → ToolTipFor',
    behavior:'菜单打开界面或请求协调退出；提示文本区分零台、一台和多台已连接设备，最长127字符。', limits:'本轮没有原生托盘截图；不能用主窗口截图代替。' },
  { id:'CONNECTION', test:/Connect|Disconnect|Reconnect|Refresh|Selection|DeviceBar|连接|刷新|耳机|设备|蓝牙|后台|退出|关闭/, label:'连接、选择、刷新和退出', scene:'MAIN-CONNECTED',
    nodes:['ConnectOrDisconnectAsync','ReconnectAsync','RefreshStateAsync','RequestCloseAsync','RequestAsync'],
    chain:'MainView / MainWindow → MainViewModel 命令、绑定或确认服务 → ClientBridge.RequestAsync → BackendPipeServer / BackendSupervisor → 单设备 Worker → NativeHeadsetService',
    behavior:'选择、连接、读取、重连和退出分开处理；提示反映后台健康、设备状态及取消结果，不能把离线缓存写成实时状态。', limits:'原始错误及共享方法的具体 UI 消费分支需按单条记录继续核对。' },
];

export function route(c) {
  const s = `${c.path} ${c.symbol} ${c.role} ${c.text}`;
  // Window ownership is stronger than text keywords such as "连接".
  if (/Calibration|FitTest|HearingSetup/.test(c.path)) return routes.find(r=>r.id==='CALIBRATION');
  if (/DeviceManager/.test(c.path)) return routes.find(r=>r.id==='MANAGER');
  if (/TrayApplication/.test(c.path)) return routes.find(r=>r.id==='TRAY');
  return routes.find(r=>r.test.test(s)) ?? {id:'DISPLAY',label:'展示文本／异常透传',scene:'UNRESOLVED',nodes:[],chain:'按来源符号及直接调用核对；尚未证明完整 UI 消费链。',behavior:'此条可能是纯展示、解析异常或内部消息；以源码和实际显示场景为准。',limits:'需要人工确认最终显示位置与条件。'};
}

const sceneSpecs = [
 ['MAIN-CONNECTED','主界面：已连接','模拟连接且有可信快照','打开主界面；查看页头、电量与功能区','当前模拟服务；能力组合见配置','app/connected-light-1280x900.png','页面级参考；并非每条异常/隐藏控件均出现在本图'],
 ['MAIN-EMPTY','主界面：无设备','模拟设备列表为空','打开界面，核对引导与空状态','所有型号共用','app/empty-light-1280x900.png','仅空列表分支'],
 ['MAIN-OFFLINE','主界面：离线缓存','模拟历史快照、当前离线','打开已有设备，核对缓存标识及禁用状态','已保存缓存的设备','app/offline-cached-light-1280x900.png','模拟缓存不代表当前设备状态'],
 ['MAIN-PARTIAL','主界面：部分未知','模拟缺少状态或能力','查看未知值与不可用控制','状态缺失/能力不完整','app/partial-light-1280x900.png','仅模拟快照；不可用原因仍按条目条件核对'],
 ['MAIN-UNSUPPORTED','主界面：不支持','模拟未支持协议/型号','查看提示和控制门控','未知/未实现协议','app/unsupported-light-1280x900.png','目录名称不证明支持'],
 ['MAIN-DISCONNECTED','主界面：断开','模拟连接中断','查看状态回退与连接入口','原连接设备','app/disconnected-light-1280x900.png','无真实断连操作'],
 ['MAIN-ANC','降噪与档位','模拟支持降噪的设备','查看模式、档位及未知子状态','由配置/codec/快照共同决定','app/anc-feature-region.png','功能区域截图；不是所有降噪配置全集'],
 ['MAIN-EQ','EQ 编辑与保存','模拟可编辑 EQ；展开均衡器','查看名称、频率、草稿和操作按钮','具备相应 EQ 能力','app/demo-light-1280x900-page02.png','演示分页作为位置参考；具体文本出现仍需逐条确认'],
 ['MAIN-FEATURE','配置驱动功能','模拟/演示能力；展开对应功能','对照演示分页与实际配置选项','不同型号/条件可能不同','app/demo-light-1280x900-page03.png','补充截图见截图目录全部 demo 分页；本图不能证明全部型号'],
 ['SETTINGS','应用设置','模拟服务；打开应用设置','依次查看外观、资源和设备入口','所有界面通用','app/settings-cards-light-1280x900.png','模拟设置卡；下载错误/成功分支未逐一截图'],
 ['MANAGER','设备管理','假的已配对设备目录','查看设备行、选择计数、刷新与保存','模拟设备目录','manager/light-520x720.png','假服务；不枚举蓝牙、不写白名单'],
 ['CAL-READY','校准：准备','模拟 calibration handle 与 Ready 状态','打开贴合度/黄金听感窗口','相应校准能力','calibration/hearing-ready.png','贴合度准备图另见 fit-ready.png'],
 ['CAL-SCAN','校准：耳道扫描','模拟耳道扫描阶段','进入扫描步骤并查看引导','支持扫描的黄金听感版本','calibration/hearing-scan.png','无真机测量'],
 ['CAL-HEARING-V1','校准：V1 回答','模拟黄金听感 V1 状态','查看响度与回答按钮','黄金听感 V1','calibration/hearing-v1.png','无真机听觉效果验证'],
 ['CAL-HEARING-V2','校准：V2 回答','模拟黄金听感 V2 状态','查看当前耳侧、频率与回答','黄金听感 V2','calibration/hearing-v2.png','无真机听觉效果验证'],
 ['CAL-RESULTS','校准：结果','模拟曲线就绪','查看结果、命名、保存和试听','具备相应结果/试听能力','calibration/hearing-results.png','原声/增强试听图另见 screenshots/calibration/；仅假播放状态'],
 ['FIT-READY','贴合度：准备','模拟贴合度 Ready','打开贴合度测试窗口','贴合度测试','calibration/fit-ready.png','与黄金听感独立窗口'],
 ['FIT-RESULTS','贴合度：结果','模拟贴合度测量结果','查看左右耳结果和重试','贴合度测试','calibration/fit-results.png','无真实密封性验证'],
 ['CAL-STOP-FAILED','校准：停止失败','假服务拒绝或未确认 STOP','尝试停止；核对错误与重试入口','所有校准取消路径','calibration/stop-failed.png','不能把本地关闭等同设备停止'],
 ['TRAY','托盘菜单与提示','静态源码；未启动真实 Host','检查打开、退出及连接数量分支','全部设备共用','', '待原生托盘检查；未连接真实设备'],
 ['TRANSIENT','等待、拒绝、超时与恢复','按具体源码条件构造假服务响应','按条目分支逐项复现','按对应功能条件','', '未逐条截图；静态链和条件保留，不算已核实'],
 ['ACCESSIBILITY','无障碍与工具提示','控件获得焦点或悬停','核对自动化名称、帮助文本与工具提示','对应控件可见时','', '屏幕阅读器输出与悬停弹层尚未原生检查'],
 ['CONFIG-NAMES','配置名称与动态设备名','内置欢律配置声明；Windows设备名可覆盖','对照配置字段、显示回退及设备标题绑定','产品名集合见对应条目','', '配置声明不是实际屏幕观测；缓存/在线增量未读取'],
 ['UNRESOLVED','显示位置待核实','暂未确认 UI 消费路径','按来源、角色和上下文核对','未知','', '必须保持待核实，不因文案审阅通过而完成'],
];

export async function prepare({baseline, output, app, calibration, manager, previous, evidence}) {
  const raw = await fs.readFile(baseline); const data = JSON.parse(raw);
  await fs.mkdir(path.join(output,'screenshots'),{recursive:true});
  const screenshots=[];
  for (const [name, dir] of Object.entries({app,calibration,manager})) {
    if (!dir) continue;
    await fs.mkdir(path.join(output,'screenshots',name),{recursive:true});
    for (const file of (await fs.readdir(dir)).filter(f=>f.endsWith('.png')).sort()) {
      const bytes=await fs.readFile(path.join(dir,file));
      const target=path.join(output,'screenshots',name,file);
      try { await fs.writeFile(target,bytes,{flag:'wx'}); }
      catch(error) { if(error.code!=='EEXIST' || hash(await fs.readFile(target))!==hash(bytes)) throw error; }
      screenshots.push({path:`screenshots/${name}/${file}`,sha256:hash(bytes),bytes:bytes.length});
    }
  }
  const scenes=sceneSpecs.map(([id,title,precondition,steps,models,image,gap])=>{
    const available=screenshots.some(s=>s.path===`screenshots/${image}`);
    return {id,title,precondition,steps,models,image:available?`screenshots/${image}`:'',gap,
      evidence:available?'离线模拟窗口截图；控件级出现未逐条确认':'静态源码；无截图'};
  });
  const methods = new Map();
  for(const m of data.methods) {
    const name=m.symbol.split('.').at(-1).split('(')[0];
    if(!methods.has(name)) methods.set(name,[]); methods.get(name).push(m);
  }
  const prior=previous?JSON.parse(await fs.readFile(previous,'utf8')):{records:[]};
  const old=new Map(prior.records.map(r=>[r.id,r]));
  const records=[];
  const candidates=data.candidates.map(c=>({...c}));
  for(const c of candidates.filter(c=>c.disposition!=='排除')) {
    const r=route(c); const owner=data.methods.find(m=>m.path===c.path && m.symbol===c.symbol);
    const links=r.nodes.flatMap(n=>(methods.get(n)??[]).filter(m=>!m.path.includes('Preview')&&!m.path.includes('Demo')));
    const bindingNames=[...`${c.text} ${c.calls.join(' ')}`.matchAll(/Binding\s+([A-Za-z_][A-Za-z0-9_]*)/g)].map(x=>x[1]);
    const bindingSources=bindingNames.flatMap(n=>(methods.get(n)??[]).filter(m=>c.path.includes('DeviceManager')?m.path.includes('DeviceManager'):m.path.includes('HeyMelody.Client/')));
    const references=[...(owner?[owner]:[]),...bindingSources,...links].map(m=>`${m.path}:${m.line}  ${m.symbol}`);
    const oldRow=old.get(c.id);
    const changed=oldRow?(oldRow.text===c.text?'未变':'原文变更'):'新增';
    let scene=r.scene;
    const combined=`${c.symbol} ${c.text} ${c.condition}`;
    if (/ToolTip|Automation|SetName/.test(c.role)) scene='ACCESSIBILITY';
    else if (/Stop|停止.*失败|停止.*确认/.test(combined) && r.id==='CALIBRATION') scene='CAL-STOP-FAILED';
    else if (/Result|试听|曲线|保存/.test(combined) && r.id==='CALIBRATION') scene='CAL-RESULTS';
    else if (/EarScan|扫描/.test(combined) && r.id==='CALIBRATION') scene='CAL-SCAN';
    else if (/超时|拒绝|未收到|失败|等待|正在|恢复|重试/.test(c.text)) scene='TRANSIENT';
    const direct=c.kind.startsWith('AXAML') || /Text|Content|Title|Description|Header|ToolTip|SetName|NativeMenuItem/.test(c.role);
    const state=direct?'显示相关来源已定位；具体消费分支待核实':'待核实最终显示分支';
    if(direct && c.disposition==='待核实') {c.disposition='已入册';c.reason='显示相关来源候选；具体消费分支及场景另列，不等于已验证';}
    const row={...c,batch:batch(c),feature:r.label,scene,route:r.id,chain:r.chain,behavior:r.behavior,
      limits:[c.condition,r.limits].filter(Boolean).join('\n'),references:[...new Set(references)],
      directCalls:(owner?.calls??c.calls).join('\n'),
      binding:c.kind==='AXAML 绑定/资源'?c.text:'',
      variables:c.kind==='动态模板'?(c.text.match(/\{[^{}]+\}/g)??[]).join('；'):'',
      example:c.kind==='动态模板'?'原模板如左；变量以模拟截图或源码为准，未枚举任意设备输入':'',
      verification:changed==='原文变更'?state:oldRow?.verification??state,review:changed==='原文变更'?'未审阅':oldRow?.review??'未审阅',suggestion:oldRow?.suggestion??'',note:oldRow?.note??'',
      change:changed,previousText:oldRow?.text??'',traceConfirmed:changed==='原文变更'?'待核实':oldRow?.traceConfirmed??'待核实',sceneConfirmed:changed==='原文变更'?'待核实':oldRow?.sceneConfirmed??'待核实'};
    records.push(row);
  }
  // Resource names have their own stable field identity and are never silently counted as code candidates.
  for(const name of data.resourceNames) {
    const id='CFG-'+hash(name.path+'|'+name.pointer).slice(0,14); const o=old.get(id);
    const changed=!!o && o.text!==name.name;
    records.push({id,identityKey:name.path+'|'+name.pointer,path:name.path,line:1,symbol:name.pointer,role:'name',text:name.name,
      kind:'配置设备名称',condition:`产品ID：${name.product??'继承父配置，见字段路径'}`,context:name.evidence,calls:[],
      disposition:'待核实',reason:name.evidence,batch:'01 主界面与连接',feature:'设备名称来源与回退',scene:'CONFIG-NAMES',route:'DISPLAY',
      chain:'ResourceCatalog.Parse → 配置名称；实际页头使用 MainViewModel.Title，需区分系统设备名称、演示名称及配置回退。',
      behavior:'保留配置声明名称供对照，不认定每个名称都会直接显示。',limits:'父子配置继承、过滤及 Windows 名称来源需逐项确认。',
      references:[`${name.path}  ${name.pointer}`],directCalls:'',binding:'',variables:'',example:'',verification:o?.verification??'配置字段已定位；可见性待核实',
      review:changed?'未审阅':o?.review??'未审阅',suggestion:o?.suggestion??'',note:o?.note??'',change:o?(changed?'原文变更':'未变'):'新增',previousText:o?.text??'',
      traceConfirmed:changed?'待核实':o?.traceConfirmed??'待核实',sceneConfirmed:changed?'待核实':o?.sceneConfirmed??'待核实'});
  }
  const present=new Set(records.map(r=>r.id));
  for(const r of prior.records.filter(r=>!present.has(r.id))) records.push({...r,change:'已删除',verification:'旧基线条目；本轮源码已不再入册'});
  const rank=r=>r.path.endsWith('/MainView.axaml')?0:r.path.endsWith('/AppSettingsPanel.axaml')?1:r.kind.startsWith('AXAML')?2:r.path.includes('/MainViewModel')?3:r.kind==='配置设备名称'?9:5;
  records.sort((a,b)=>a.batch.localeCompare(b.batch,'zh-CN')||rank(a)-rank(b)||a.path.localeCompare(b.path)||a.line-b.line||a.id.localeCompare(b.id));
  const verificationEvidence=evidence?JSON.parse(await fs.readFile(evidence,'utf8')):{summary:'未提供本轮界面测试执行收据；截图仅作为来源目录提供的参考。',nativeExcelVerified:false};
  const result={schemaVersion:1,verificationEvidence,baseline:{commit:data.commit,workingTree:data.workingTree,appVersion:data.appVersion,createdUtc:data.createdUtc,
    sha256:hash(raw),boundary:data.boundary},records,scenes,screenshots,candidates,files:data.files,
    counts:{files:data.files.length,candidates:candidates.length,excluded:candidates.filter(c=>c.disposition==='排除').length,
      included:candidates.filter(c=>c.disposition==='已入册').length,pending:candidates.filter(c=>c.disposition==='待核实').length,
      resources:data.resourceNames.length,records:records.length,screenshots:screenshots.length}};
  assert.equal(new Set(records.map(r=>r.id)).size,records.length,'duplicate text ID');
  assert.equal(result.counts.candidates,result.counts.excluded+result.counts.included+result.counts.pending);
  assert(records.every(r=>scenes.some(s=>s.id===r.scene)),'dangling scene');
  const dataFile=path.join(output,'audit-data.json');
  // Rebuild generated inputs safely; reviewed workbooks are never consumed implicitly or overwritten.
  await fs.writeFile(dataFile,JSON.stringify(result,null,2));
  await fs.writeFile(path.join(output,'screenshots','index.html'),`<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>审计截图</title><style>body{font:16px sans-serif;max-width:1400px;margin:auto}img{max-width:100%;border:1px solid #ddd}a{color:#075985}</style><h1>离线模拟界面截图</h1><p>无真机效果验证；截图仅证明对应假服务输入下的界面。</p>${screenshots.map(s=>`<p><a href="${s.path.replace('screenshots/','')}">${s.path}</a></p><img loading="lazy" src="${s.path.replace('screenshots/','')}">`).join('')}</html>`);
  console.log(JSON.stringify(result.counts));
  return result;
}

if(process.argv[2]==='--self-test') {
  assert.equal(route({path:'src/HeyMelody.App/MainView.axaml',symbol:'SaveEqCommand',role:'Content',text:'保存并启用'}).id,'EQ-SAVE');
  assert.equal(route({path:'src/HeyMelody.Calibration.UI/CalibrationView.cs',symbol:'Render',role:'Content',text:'连接'}).id,'CALIBRATION');
  assert.equal(route({path:'src/HeyMelody.DeviceManager/DeviceManagerViewModel.cs',symbol:'SaveAsync',role:'Text',text:'连接'}).id,'MANAGER');
  console.log('PASS source ownership and EQ-save route selection');
  const testRoot=await fs.mkdtemp(path.resolve('artifacts/ui-text-audit-selftest-'));
  const c={id:'stable',identityKey:'stable',path:'src/HeyMelody.App/MainView.axaml',line:1,symbol:'Save',role:'Content',text:'旧文案',kind:'AXAML 固定文本',condition:'',context:'',calls:[],disposition:'已入册',reason:'测试'};
  const fixture={commit:'test',workingTree:'',appVersion:'test',createdUtc:'2026-10-19T00:00:00Z',boundary:'fixture',files:[],methods:[],resourceNames:[],candidates:[c]};
  const baseline=path.join(testRoot,'baseline.json');await fs.writeFile(baseline,JSON.stringify(fixture));
  const first=await prepare({baseline,output:path.join(testRoot,'first')});
  first.records[0].review='通过';first.records[0].traceConfirmed='已核实';first.records[0].sceneConfirmed='已核实';first.records[0].suggestion='建议';first.records[0].note='备注';
  first.records.push({...first.records[0],id:'removed'});
  const previous=path.join(testRoot,'previous.json');await fs.writeFile(previous,JSON.stringify(first));
  const unchanged=await prepare({baseline,output:path.join(testRoot,'unchanged'),previous});
  assert.equal(unchanged.records.find(r=>r.id==='stable').review,'通过');
  assert.equal(unchanged.records.find(r=>r.id==='removed').change,'已删除');
  fixture.candidates[0].text='新文案';await fs.writeFile(baseline,JSON.stringify(fixture));
  const changed=await prepare({baseline,output:path.join(testRoot,'changed'),previous});
  const row=changed.records.find(r=>r.id==='stable');
  assert.equal(row.review,'未审阅');assert.equal(row.traceConfirmed,'待核实');assert.equal(row.sceneConfirmed,'待核实');
  assert.equal(row.suggestion,'建议');assert.equal(row.note,'备注');assert.equal(row.previousText,'旧文案');
  assert(changed.scenes.every(s=>!s.image && s.evidence==='静态源码；无截图'));
  console.log('PASS review preservation, changed-text reset, deleted history, and missing-image evidence');
} else if(process.argv[1] && import.meta.url===new URL(`file:///${process.argv[1].replaceAll('\\','/')}`).href) {
  const [baseline,output,app,calibration,manager,previous,evidence]=process.argv.slice(2);
  await prepare({baseline,output,app,calibration,manager,previous,evidence});
}
