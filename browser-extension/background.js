let frames=new Map(),busy=false,ack=null,generation=crypto.randomUUID().replaceAll('-','');
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
 if(sender.id!==chrome.runtime.id)return;
 if(message.kind==='is-target'){chrome.storage.local.get('target').then(({target})=>reply(target===sender.tab?.id));return true;}
 if(message.kind==='stop'){
  chrome.storage.local.remove('target').then(()=>{frames.clear();reply({done:true});});return true;
 }
 if(message.kind!=='report'||!sender.tab?.id)return;
 handle(message,sender).then(()=>reply({done:true})).catch(()=>reply({done:false}));return true;
});
async function handle(message,sender){
 const {bridge,target}=await chrome.storage.local.get(['bridge','target']);
 if(!bridge||target!==sender.tab.id)return;
 const snapshot=message.report;
 if(!snapshot||typeof snapshot.title!=='string'||!Array.isArray(snapshot.actions))return;
 frames.set(sender.frameId,{...snapshot,seen:Date.now()});
 if(busy)return;busy=true;
 try {
  for(const [id,frame]of frames)if(Date.now()-frame.seen>4000)frames.delete(id);
  const sources=[...frames].sort((a,b)=>(b[1].area||0)-(a[1].area||0));
  const actions=[...new Set(sources.flatMap(([,frame])=>frame.actions))].filter(action=>['play','pause','seek-back','seek-forward','skip-intro','skip-ad','next-episode'].includes(action));
  const window=sources.some(([,frame])=>frame.area>0)?await chrome.windows.get(sender.tab.windowId).catch(()=>null):null;
  if(window)actions.push(window.state==='fullscreen'?'exit-fullscreen':'fullscreen');
  const response=await fetch(`http://127.0.0.1:${bridge.port}/browser/poll`,{method:'POST',headers:{'Content-Type':'application/json','Authorization':'Bearer '+bridge.token},body:JSON.stringify({title:(sources[0]?.[1].title||'Вкладка браузера').slice(0,140),actions,commandId:ack?.id,success:ack?.success,generation}),signal:AbortSignal.timeout(3000)});
  if(!response.ok){if(response.status===401)await chrome.storage.local.remove('bridge');return;}
  ack=null;const {command}=await response.json();
  if(command){const current=await chrome.storage.local.get('target');
   if(current.target===target&&command.generation===generation&&actions.includes(command.action)&&['fullscreen','exit-fullscreen'].includes(command.action)){
    try{ack={id:command.id,success:await fullscreen(target,command)};}catch{ack={id:command.id,success:false};}return;
   }
   const source=current.target===target&&command.generation===generation?sources.find(([,frame])=>frame.actions.includes(command.action)):null;
   if(!source)ack={id:command.id,success:false};
   else try{ack=await chrome.tabs.sendMessage(target,{kind:'execute',command,page:source[1].page},{frameId:source[0]});}catch{ack={id:command.id,success:false};}
  }
 }finally{busy=false;}
}
async function fullscreen(target,command){
 const tab=await chrome.tabs.get(target),window=await chrome.windows.get(tab.windowId);
 const key='window-restore:'+window.id,stored=await chrome.storage.local.get(['target',key]);
 if(stored.target!==target||command.generation!==generation)return false;
 const enter=command.action==='fullscreen';
 if(enter&&window.state==='fullscreen'||!enter&&window.state!=='fullscreen')return true;
 if(enter)await chrome.storage.local.set({[key]:window.state==='maximized'?'maximized':'normal'});
 const current=await chrome.storage.local.get('target');if(current.target!==target||command.generation!==generation)return false;
 const state=enter?'fullscreen':stored[key]==='maximized'?'maximized':'normal';
 await chrome.tabs.update(target,{active:true});
 const latest=await chrome.storage.local.get('target');if(latest.target!==target||command.generation!==generation)return false;
 const updated=await chrome.windows.update(window.id,{state,focused:true});
 if(!enter)await chrome.storage.local.remove(key);
 return updated.state===state;
}
chrome.tabs.onRemoved.addListener(async id=>{const {target}=await chrome.storage.local.get('target');if(target===id){await chrome.storage.local.remove('target');frames.clear();}});
chrome.tabs.onUpdated.addListener((id,change)=>{if(change.status==='loading')chrome.storage.local.get('target').then(({target})=>{if(target===id){frames.clear();ack=null;generation=crypto.randomUUID().replaceAll('-','');}});});
chrome.storage.onChanged.addListener((changes,area)=>{if(area==='local'&&changes.target){frames.clear();ack=null;generation=crypto.randomUUID().replaceAll('-','');}});
chrome.webNavigation.onDOMContentLoaded.addListener(async details=>{
 const {target}=await chrome.storage.local.get('target');
 if(target!==details.tabId||!/^https?:/.test(details.url))return;
 await chrome.scripting.executeScript({target:{tabId:target,frameIds:[details.frameId]},files:['player.js','content.js']}).catch(()=>{});
});
