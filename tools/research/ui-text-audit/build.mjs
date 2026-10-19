import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';

process.on('uncaughtException',error=>{console.error(`${error.name}: ${String(error.message).slice(0,700)}`);process.exit(1);});
process.on('unhandledRejection',error=>{console.error(`Build failed: ${String(error?.message??error).slice(0,700)}`);process.exit(1);});

const [folder, dependencyRoot] = process.argv.slice(2);
if(!folder || !dependencyRoot) throw new Error('Usage: node build.mjs <output-directory> <runtime-node-modules>');
const output=path.resolve(folder);
const require=createRequire(path.join(path.resolve(dependencyRoot),'..','audit-loader.cjs'));
const {Workbook,SpreadsheetFile}=await import(pathToFileURL(require.resolve('@oai/artifact-tool')));
const data=JSON.parse(await fs.readFile(path.join(output,'audit-data.json'),'utf8'));
const workbook=Workbook.create();
const sheets=['文本审阅','功能追踪','场景证据','覆盖清单'].map(name=>workbook.worksheets.add(name));
const links=[];
const font='Microsoft YaHei UI';
const display=v=>typeof v==='string'?v.replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\uFFFE\uFFFF]/g,c=>'\\u'+c.charCodeAt(0).toString(16).padStart(4,'0')):v??'';
const literal=v=>typeof v==='string' && (/^[=+@]/.test(v)||/^\d{4}-\d\d-\d\dT/.test(v))?"'"+display(v):display(v);
function col(n){let s='';for(n++;n;n=Math.floor((n-1)/26))s=String.fromCharCode(65+(n-1)%26)+s;return s;}
function writeTable(sheet,index,headers,rows,widths) {
  const end=rows.length+6,last=col(headers.length-1);
  sheet.showGridLines=false; sheet.tabColor=['#155E75','#475569','#0F766E','#92400E'][index];
  const used=sheet.getRange(`A1:${last}${end}`);
  used.format.font={name:font,size:10,color:'#172B3A'};
  used.format.wrapText=true; used.format.verticalAlignment='top';
  sheet.getRange('A1').values=[[['界面文本人工审计','源码与实际执行追踪','场景与截图证据','基线、覆盖与审计进度'][index]]];
  sheet.getRange(`A1:${last}1`).format.fill='#123247';
  sheet.getRange(`A1:${last}1`).format.font={name:font,size:15,bold:true,color:'#FFFFFF'};
  sheet.getRange('A1:A1').format.rowHeight=30;
  sheet.getRange('D2').values=[['基线 '+data.baseline.appVersion+' · '+data.baseline.commit.slice(0,12)+' · '+data.baseline.createdUtc]];
  sheet.getRange('A2').format.columnWidth=26;
  sheet.getRange(`A2:${last}2`).format.rowHeight=30;
  sheet.getRange('D3').values=[[[
    '顺序：01连接 → 02功能 → 03设置/设备 → 04校准 → 05辅助。黄色列供填写；点击场景/追踪编号跳转。',
    '调用链按功能分组；“源码定位”与“直接调用”是本条证据。共享接口与消费分支仍须逐条确认。',
    '截图均来自离线假服务。页面级参考不证明所有条目出现；无截图及未逐条验证项保持待核实。',
    '候选总账包含排除理由；“待核实”不等于已确认显示。未读取在线/缓存增量及个人设备快照。'
  ][index]]];
  // Keep notes in ordinary wide cells without merged table data.
  sheet.getRange(`A3:${last}3`).format.rowHeight=82;
  sheet.getRange(`A6:${last}6`).values=[headers];
  sheet.getRange(`A7:${last}${end}`).values=rows.map(row=>row.map(literal));
  const table=sheet.tables.add(`A6:${last}${end}`,true,['ReviewRows','TraceRows','SceneRows','CoverageRows'][index]);
  table.showFilterButton=true;
  sheet.getRange(`A6:${last}6`).format.fill='#155E75';
  sheet.getRange(`A6:${last}6`).format.font={name:font,bold:true,color:'#FFFFFF',size:10};
  sheet.getRange(`A6:${last}6`).format.rowHeight=32;
  for(let i=0;i<widths.length;i++) sheet.getRange(`${col(i)}:${col(i)}`).format.columnWidth=widths[i];
  sheet.getRange(`A7:${last}${end}`).format.rowHeight=index===1?160:index===0?54:75;
  sheet.freezePanes.freezeRows(6);sheet.freezePanes.freezeColumns(index===0?4:1);
  return end;
}

const sceneRows=new Map(data.scenes.map((s,i)=>[s.id,i+7]));
const reviewRows=data.records.map((r,i)=>[r.id,r.batch,`${r.symbol}\n${r.role} · ${r.kind}`,r.text,r.feature,r.scene,r.id,
  r.traceConfirmed,r.sceneConfirmed,r.review,r.suggestion,r.note,r.change,'',r.verification,
  [r.variables,r.example,r.previousText&&r.change==='原文变更'?'旧原文：'+r.previousText:''].filter(Boolean).join('\n')]);
const end=writeTable(sheets[0],0,['文本 ID','审阅批次','控件／符号与文本类型','当前原文／模板','功能说明','场景入口','调用追踪入口',
  '功能链核实','场景核实','措辞结论','建议文案','人工备注','基线变化','整体待办','证据状态','变量／示例／旧原文'],reviewRows,
  [25,22,40,64,30,24,25,15,15,15,48,48,14,18,40,48]);
const main=sheets[0];
for(let i=0;i<reviewRows.length;i++) {
  const cellLines=(text,width)=>String(text).split('\n').reduce((n,line)=>n+Math.max(1,Math.ceil([...line].reduce((a,c)=>a+(c.charCodeAt(0)>255?2:1),0)/(width-2))),0);
  const lines=Math.max(cellLines(reviewRows[i][2],40),cellLines(reviewRows[i][3],64));
  if(lines>3) main.getRange(`A${i+7}:P${i+7}`).format.rowHeight=Math.min(409,lines*14+10);
}
main.getRange(`H7:L${end}`).format.fill='#FFF4CC';
main.getRange(`H7:I${end}`).dataValidation={rule:{type:'list',values:['待核实','已核实','不适用']}};
main.getRange(`J7:J${end}`).dataValidation={rule:{type:'list',values:['未审阅','通过','需修改','需讨论']}};
main.getRange(`N7:N${end}`).formulas=data.records.map((r,i)=>{
  const n=i+7;
  return [`=IF(M${n}="已删除","历史条目",IF(J${n}="未审阅","待审阅",IF(J${n}="需修改","待修订",IF(J${n}="需讨论","待讨论",IF(OR(H${n}="待核实",I${n}="待核实",H${n}="",I${n}=""),"待核实","审阅完成")))))`];
});
main.getRange(`J7:J${end}`).conditionalFormats.addCustom('J7="需修改"',{fill:'#FEE2E2',font:{color:'#991B1B'}});
main.getRange(`N7:N${end}`).conditionalFormats.addCustom('N7="待核实"',{fill:'#FEF3C7',font:{color:'#92400E'}});
main.getRange(`N7:N${end}`).conditionalFormats.addCustom('N7="审阅完成"',{fill:'#DCFCE7',font:{color:'#166534'}});
main.getRange('D4').formulas=[[`="待审阅："&COUNTIF(J7:J${end},"未审阅")&"　待修订："&COUNTIF(J7:J${end},"需修改")&"　审阅完成："&COUNTIF(N7:N${end},"审阅完成")`]];
main.getRange('D4').format.rowHeight=32;

const traceRows=data.records.map(r=>[r.id,r.path,`${r.symbol}\n行 ${r.line}`,r.role,
  [r.binding,...r.calls].filter(Boolean).join('\n'),r.limits,r.chain,r.behavior,
  '提交成功、ACK 与设备回读分别核实；纯展示条目不适用设备写入判断。',r.directCalls,
  r.references.join('\n'),r.reason,r.context]);
writeTable(sheets[1],1,['文本 ID','源码文件（点击打开）','符号与行号','显示属性／参数位置','绑定／事件／命令','条件与适用边界','功能分组参考路径（待逐条确认）',
  '实际行为','结果核对要求','所属符号的直接调用（语法证据）','执行节点的源码位置','入册依据／未闭合点','源表达式上下文'],traceRows,
  [25,54,44,25,42,70,95,65,44,55,80,65,110]);

const sceneData=data.scenes.map(s=>[s.id,s.title,s.precondition,s.steps,s.models,s.image||'无截图；保持待核实',s.evidence,s.gap]);
writeTable(sheets[2],2,['场景 ID','窗口与状态','前置条件','操作步骤与核对位置','型号／能力条件','截图（点击打开）','证据方式','未验证与补充说明'],sceneData,
  [25,30,50,64,50,65,50,80]);

const coverage=[];
const info=(key,value,note='')=>coverage.push(['基线/说明',key,value,'','','','','',note]);
info('Git 提交',data.baseline.commit);info('应用版本',data.baseline.appVersion);
info('提取时间 UTC',data.baseline.createdUtc);info('基线 JSON SHA256',data.baseline.sha256);
info('工作区状态',data.baseline.workingTree||'干净');info('输入范围',data.baseline.boundary);
info('使用说明','黄色列填写 H/I/J/K/L；先审措辞，再分别确认功能链和场景。');
info('更新与意见保留','按稳定 ID 读取旧 Excel；变更条目保留旧原文，删除条目进入历史。插入相同结构分支可能改变局部编号，须核对新增/删除。');
info('验证边界',data.verificationEvidence.summary,'本轮未完成原生 Excel、屏幕阅读器、托盘或真实耳机验证。');
info('候选总数',data.counts.candidates,'等于入册候选＋待核实候选＋排除候选；配置名称另计。');
info('已入册候选',data.counts.included); info('待核实候选',data.counts.pending);info('排除候选',data.counts.excluded);
info('配置名称',data.counts.resources);info('主表记录',data.records.length);info('配套截图',data.screenshots.length);
info('缓存/在线配置增量','未取得','当前内置配置是固定基线，不能声称覆盖任意后续在线文案。');
info('源码链接','相对当前输出目录定位到仓库源码；行号和符号另列。','移动整个仓库仍可用；单独复制交付目录后源码链接需按源码文件列手动定位。');
info('截图浏览','screenshots/index.html','与 Excel 一同保留 screenshots 子目录；表内链接为相对路径。');
for(const name of [...new Set(data.records.map(r=>r.batch))]) coverage.push(['批次进度',name,data.records.filter(r=>r.batch===name).length,'未审阅','','','','','筛选主表 B 列进入该批次。']);
for(const f of data.files) coverage.push(['文件覆盖',f.path,f.candidates,'已扫描',`${f.bytes} bytes；${f.chars} chars；最长行 ${f.longest}`,f.sha256,'','',f.category]);
for(const c of data.candidates) coverage.push(['候选总账',c.id,c.text,c.disposition,`${c.path}:${c.line}`,c.symbol,c.role,c.reason,c.condition]);
for(const s of data.screenshots) coverage.push(['截图文件',s.path,s.bytes,'离线模拟',s.sha256,'','','','截图仅证明对应模拟输入的布局与内容。']);
const coverageEnd=writeTable(sheets[3],3,['记录类别','编号／文件／事项','原文／数量／基线值','处置／状态','位置／大小','符号／SHA256','角色／属性','依据／排除理由','边界／未闭合问题'],coverage,
  [20,65,85,22,70,80,35,85,85]);
for(let i=0;i<coverage.length;i++) if(coverage[i][0]==='批次进度') {
  const name=coverage[i][1].replaceAll('"','""');
  sheets[3].getRange(`D${i+7}`).formulas=[[`="待办 "&COUNTIFS('文本审阅'!B7:B${end},"${name}",'文本审阅'!N7:N${end},"<>审阅完成")`]];
}

for(let i=0;i<data.records.length;i++) {
  const n=i+7,r=data.records[i];
  links.push({sheet:0,cell:`F${n}`,location:`'场景证据'!A${sceneRows.get(r.scene)}`},
    {sheet:0,cell:`G${n}`,location:`'功能追踪'!A${n}`},
    {sheet:1,cell:`A${n}`,location:`'文本审阅'!A${n}`},
    {sheet:1,cell:`B${n}`,target:path.relative(output,path.resolve(r.path)).replaceAll('\\','/')});
}
for(let i=0;i<data.scenes.length;i++) if(data.scenes[i].image) links.push({sheet:2,cell:`F${i+7}`,target:data.scenes[i].image});
// A narrow status formula is checked before final export, with incomplete evidence deliberately left open.
const saved=main.getRange('J7').values;
main.getRange('J7').values=[['通过']];workbook.recalculate();
assert.equal(main.getRange('N7').values[0][0],'待核实','wording alone must not complete the row');
main.getRange('H7:I7').values=[['已核实','已核实']];workbook.recalculate();
assert.equal(main.getRange('N7').values[0][0],'审阅完成','all prerequisites should complete row');
main.getRange('J7').values=saved;main.getRange('H7:I7').values=[[data.records[0].traceConfirmed,data.records[0].sceneConfirmed]];
workbook.recalculate();
const formulaErrors=[];
for(const sheet of sheets) {
  const values=sheet.getUsedRange().values;
  for(let i=0;i<values.length;i++)for(let j=0;j<values[i].length;j++)if(typeof values[i][j]==='string' && /^#(REF!|DIV\/0!|VALUE!|NAME\?|N\/A|NUM!|SPILL!|CALC!)/.test(values[i][j])) formulaErrors.push({sheet:sheet.name,row:i+1,col:j+1});
}
assert.equal(formulaErrors.length,0,'formula error scan');
const inspection=await workbook.inspect({kind:'sheet',include:'id,name',sheetId:sheets[0].id,range:'H6:N9'});
await fs.writeFile(path.join(output,'inspection.json'),JSON.stringify(inspection,null,2));
await fs.mkdir(path.join(output,'previews'),{recursive:true});
for(let i=0;i<sheets.length;i++) {
  const ranges=['A6:F11','A6:F9','A6:D10','A6:D13'];
  const blob=await workbook.render({sheetName:sheets[i].name,range:ranges[i],scale:1.25,format:'png'});
  await fs.writeFile(path.join(output,'previews',`${i+1}.png`),new Uint8Array(await blob.arrayBuffer()));
}
for(const [file,sheetName,range] of [['instructions','文本审阅','A1:F6'],['review-fields','文本审阅','H6:N10']]) {
  const blob=await workbook.render({sheetName,range,scale:1.25,format:'png'});
  await fs.writeFile(path.join(output,'previews',`${file}.png`),new Uint8Array(await blob.arrayBuffer()));
}
const xlsx=await SpreadsheetFile.exportXlsx(workbook);
await xlsx.save(path.join(output,'界面文本审计.xlsx'));
await fs.writeFile(path.join(output,'links.json'),JSON.stringify(links));
await fs.writeFile(path.join(output,'builder-checks.json'),JSON.stringify({rows:data.records.length,coverageRows:coverage.length,formulaErrors,
  incompleteEvidenceStaysPending:true,confirmedEvidenceCompletes:true,renderedSheets:4,nativeExcelVerified:false},null,2));
console.log(JSON.stringify({xlsx:path.join(output,'界面文本审计.xlsx'),rows:data.records.length,coverageRows:coverage.length,formulaErrors:0}));
