let frames=new Map(),busy=false,ack=null,generation=crypto.randomUUID().replaceAll('-','');
let nextSync=0,failures=0;
function retryLater(){nextSync=Date.now()+Math.min(15000,1000*2**Math.min(++failures,4));}
const origins=['http://*/*','https://*/*'];
const mediaActions=['play','pause','seek-back','seek-forward','skip-intro','skip-ad','next-episode','set-volume','mute-player','unmute-player'];
const managementActions=['select-tab','focus-tab','refresh-tabs','detach-tab'];
function eligible(tab){return Number.isInteger(tab?.id)&&tab.id>0&&!tab.incognito&&/^https?:\/\//.test(tab.url||'');}
function newPage(){frames.clear();generation=crypto.randomUUID().replaceAll('-','');}
function metadata(tab){const hostname=new URL(tab.url).hostname,title=(tab.title||'').replace(/[\u0000-\u001f\u007f]/g,' ').replace(/\s+/g,' ').trim()||hostname;return{id:tab.id,title:title.slice(0,100),domain:(hostname.includes(':')?'IPv6-сайт':hostname).slice(0,120),audible:!!tab.audible};}
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
 if(sender.id!==chrome.runtime.id)return;
 if(message.kind==='is-target'){chrome.storage.local.get('target').then(({target})=>reply(target===sender.tab?.id&&!sender.tab?.incognito));return true;}
 if(message.kind==='stop'){chrome.storage.session.remove('lastTab').then(()=>chrome.storage.local.remove(['target','tabSharing'])).then(()=>{newPage();void sync();reply({done:true});});return true;}
 if(message.kind==='connect-tab'){chrome.storage.local.get('bridge').then(async({bridge})=>reply({done:!!bridge&&await connectTab(message.tabId)})).catch(()=>reply({done:false}));return true;}
 if(message.kind==='wake'){wake();reply({done:true});return;}
 if(message.kind!=='report'||!sender.tab?.id)return;
 handle(message,sender).then(()=>reply({done:true})).catch(()=>reply({done:false}));return true;
});
async function handle(message,sender){
 const{bridge,target}=await chrome.storage.local.get(['bridge','target']);
 if(!bridge||target!==sender.tab.id||sender.tab.incognito)return;
 const snapshot=message.report;
 if(!snapshot||typeof snapshot.title!=='string'||!Array.isArray(snapshot.actions))return;
 frames.set(sender.frameId,{...snapshot,tab:sender.tab.id,seen:Date.now()});
 if(snapshot.area>0){const saved=await chrome.storage.session.get('lastTab');if(saved.lastTab!==sender.tab.id)await chrome.storage.session.set({lastTab:sender.tab.id});}
 await sync();
}
async function sync(){
 if(busy||Date.now()<nextSync)return;busy=true;
 try{
  const{bridge,target,tabSharing}=await chrome.storage.local.get(['bridge','target','tabSharing']);if(!bridge)return;
  const sharing=!!tabSharing&&await chrome.permissions.contains({origins});
  let catalog=null;
  if(sharing)catalog=(await chrome.tabs.query({url:origins})).filter(eligible).sort((a,b)=>(b.id===target)-(a.id===target)||Number(!!b.audible)-Number(!!a.audible)||Number(!!b.active)-Number(!!a.active)||a.id-b.id).slice(0,200);
  const tab=target?await chrome.tabs.get(target).catch(()=>null):null,selected=eligible(tab)?target:null;
  const pageGeneration=generation;
  for(const[id,frame]of frames)if(frame.tab===selected&&Date.now()-frame.seen>1500&&(frame.area>0||frame.actions?.length)){
   let timeout;
   try{
    const snapshot=await Promise.race([chrome.tabs.sendMessage(selected,{kind:'inspect',page:frame.page},{frameId:id}),new Promise(resolve=>{timeout=setTimeout(()=>resolve(null),1200);})]);
    if(snapshot?.page===frame.page&&typeof snapshot.title==='string'&&Array.isArray(snapshot.actions)&&pageGeneration===generation&&frames.get(id)===frame)frames.set(id,{...snapshot,tab:selected,seen:Date.now()});
   }catch{}finally{clearTimeout(timeout);}
  }
  for(const[id,frame]of frames)if(frame.tab!==selected||Date.now()-frame.seen>4000)frames.delete(id);
  const sources=[...frames].sort((a,b)=>(b[1].area||0)-(a[1].area||0));
  const actions=[...new Set(sources.flatMap(([,frame])=>frame.actions))].filter(action=>mediaActions.includes(action));
  const window=selected&&sources.some(([,frame])=>frame.area>0)?await chrome.windows.get(tab.windowId).catch(()=>null):null;
  if(window)actions.push(window.state==='fullscreen'?'exit-fullscreen':'fullscreen');
  const saved=await chrome.storage.session.get('lastTab');
  const resumeTab=sharing&&catalog?.some(tab=>tab.id===saved.lastTab)?saved.lastTab:null;
  if(sharing&&saved.lastTab&&!resumeTab)await chrome.storage.session.remove('lastTab');
  const player=sources.find(([,frame])=>frame.actions.includes('set-volume'))?.[1];
  const volume=Number.isInteger(player?.volume)&&player.volume>=0&&player.volume<=100&&typeof player.muted==='boolean'?player.volume:null;
  const epoch=generation;
  const report={title:(sources[0]?.[1].title||(selected?tab.title:'')||'').slice(0,140),actions,commandId:ack?.id,success:ack?.success,generation:epoch,tabs:catalog?.map(metadata)??null,selectedTab:selected,resumeTab,volume,muted:volume===null?null:player.muted};
  const response=await fetch(`http://127.0.0.1:${bridge.port}/browser/poll`,{method:'POST',headers:{'Content-Type':'application/json','Authorization':'Bearer '+bridge.token},body:JSON.stringify(report),signal:AbortSignal.timeout(3000)});
  if(!response.ok){retryLater();if(response.status===401){const latest=await chrome.storage.local.get('bridge');if(latest.bridge?.token===bridge.token&&latest.bridge?.port===bridge.port){await chrome.storage.local.remove('bridge');newPage();}}return;}
  nextSync=0;failures=0;
  ack=null;const{command}=await response.json();if(!command)return;
  const current=await chrome.storage.local.get(['target','tabSharing','bridge']);
  if(current.bridge?.token!==bridge.token||current.bridge?.port!==bridge.port||command.generation!==generation||epoch!==generation||current.target!==target){ack={id:command.id,success:false};return;}
  try{
   if(managementActions.includes(command.action)){ack={id:command.id,success:sharing&&!!current.tabSharing&&await manage(command,catalog)};return;}
   if(!selected||!actions.includes(command.action)||command.tabId!=null&&command.tabId!==selected){ack={id:command.id,success:false};return;}
   if(['fullscreen','exit-fullscreen'].includes(command.action)){ack={id:command.id,success:await fullscreen(target,command)};return;}
   const source=sources.find(([,frame])=>frame.actions.includes(command.action));
   ack=source?await chrome.tabs.sendMessage(target,{kind:'execute',command,page:source[1].page},{frameId:source[0]}):{id:command.id,success:false};
  }catch{ack={id:command.id,success:false};}
 }catch{retryLater();}finally{busy=false;}
}
async function manage(command,catalog){
 if(command.action==='refresh-tabs')return true;
 if(command.action==='detach-tab'){await chrome.storage.local.remove('target');newPage();return true;}
 if(!catalog?.some(tab=>tab.id===command.tabId))return false;
 const tab=await chrome.tabs.get(command.tabId),current=await chrome.storage.local.get('tabSharing');
 if(!eligible(tab)||!current.tabSharing||command.generation!==generation)return false;
 if(command.action==='focus-tab'){await chrome.tabs.update(tab.id,{active:true});const latest=await chrome.storage.local.get('tabSharing');if(!latest.tabSharing||command.generation!==generation)return false;await chrome.windows.update(tab.windowId,{focused:true});return true;}
 return await connectTab(tab.id);
}
async function connectTab(tabId){
 const tab=await chrome.tabs.get(tabId);if(!eligible(tab))return false;
 await chrome.storage.local.set({target:tabId});newPage();
 const epoch=generation;
 // One inaccessible embedded frame must not prevent the main player from connecting.
 const documents=chrome.webNavigation.getAllFrames?await chrome.webNavigation.getAllFrames({tabId}).catch(()=>[]):[{frameId:0,url:tab.url}];
 const ids=[...new Set([0,...(documents||[]).filter(frame=>/^https?:/.test(frame.url)).map(frame=>frame.frameId)])];
 const results=await Promise.all(ids.map(async frameId=>{
  try{
   await chrome.scripting.executeScript({target:{tabId,frameIds:[frameId]},files:['player.js','content.js']});
   const snapshot=await chrome.tabs.sendMessage(tabId,{kind:'probe'},{frameId});
   const current=await chrome.storage.local.get('target');
   if(current.target!==tabId||generation!==epoch)return false;
   if(snapshot&&typeof snapshot.page==='string'&&typeof snapshot.title==='string'&&Array.isArray(snapshot.actions)){
    frames.set(frameId,{...snapshot,tab:tabId,seen:Date.now()});return true;
   }
  }catch{}
  return false;
 }));
 const current=await chrome.storage.local.get('target');
 if(current.target!==tabId||generation!==epoch)return false;
 if(results.some(Boolean)){if([...frames.values()].some(frame=>frame.tab===tabId&&frame.area>0))await chrome.storage.session.set({lastTab:tabId});return true;}
 await chrome.storage.local.remove('target');return false;
}
async function fullscreen(target,command){
 const tab=await chrome.tabs.get(target),window=await chrome.windows.get(tab.windowId);
 const key='window-restore:'+window.id,stored=await chrome.storage.local.get(['target',key]);
 if(stored.target!==target||command.generation!==generation)return false;
 const enter=command.action==='fullscreen';if(enter&&window.state==='fullscreen'||!enter&&window.state!=='fullscreen')return true;
 if(enter)await chrome.storage.local.set({[key]:window.state==='maximized'?'maximized':'normal'});
 const current=await chrome.storage.local.get('target');if(current.target!==target||command.generation!==generation)return false;
 const state=enter?'fullscreen':stored[key]==='maximized'?'maximized':'normal';
 await chrome.tabs.update(target,{active:true});
 const latest=await chrome.storage.local.get('target');if(latest.target!==target||command.generation!==generation)return false;
 const updated=await chrome.windows.update(window.id,{state,focused:true});if(!enter)await chrome.storage.local.remove(key);return updated.state===state;
}
chrome.tabs.onRemoved.addListener(async id=>{const{lastTab}=await chrome.storage.session.get('lastTab');if(lastTab===id)await chrome.storage.session.remove('lastTab');const{target}=await chrome.storage.local.get('target');if(target===id){await chrome.storage.local.remove('target');newPage();}void sync();});
chrome.tabs.onUpdated.addListener((id,change)=>{if(change.status==='loading')chrome.storage.local.get('target').then(({target})=>{if(target===id)newPage();});});
chrome.storage.onChanged.addListener((changes,area)=>{if(area==='local'&&(changes.target||changes.tabSharing||changes.bridge)){newPage();nextSync=0;failures=0;if(changes.bridge)void resume();else void sync();}});
chrome.webNavigation.onDOMContentLoaded.addListener(async details=>{
 const{target}=await chrome.storage.local.get('target');if(target!==details.tabId||!/^https?:/.test(details.url))return;
 await chrome.scripting.executeScript({target:{tabId:target,frameIds:[details.frameId]},files:['player.js','content.js']}).catch(()=>{});
});
// Keep the fast loop only while paired; an alarm recovers worker suspension.
let ticker=null;
async function resume(pollNow=true){
 const{bridge}=await chrome.storage.local.get('bridge');
 if(!bridge){if(ticker!==null)clearInterval(ticker);ticker=null;await chrome.alarms.clear('parallexity-sync');return;}
 if(ticker===null)ticker=setInterval(()=>void sync(),1000);
 const alarm=await chrome.alarms.get('parallexity-sync');if(!alarm)await chrome.alarms.create('parallexity-sync',{periodInMinutes:.5});
 if(pollNow)void sync();
}
chrome.alarms.onAlarm.addListener(alarm=>{if(alarm.name==='parallexity-sync')void sync();});
function wake(){nextSync=0;failures=0;void resume();}
chrome.runtime.onStartup.addListener(async()=>{await chrome.storage.session.remove('lastTab');await chrome.storage.local.remove('target');newPage();wake();});chrome.runtime.onInstalled.addListener(wake);
void resume(false);
