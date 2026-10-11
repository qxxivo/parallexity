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
  const response=await fetch(`http://127.0.0.1:${bridge.port}/browser/poll`,{method:'POST',headers:{'Content-Type':'application/json','Authorization':'Bearer '+bridge.token},body:JSON.stringify({title:(sources[0]?.[1].title||'Вкладка браузера').slice(0,140),actions,commandId:ack?.id,success:ack?.success,generation}),signal:AbortSignal.timeout(3000)});
  if(!response.ok){if(response.status===401)await chrome.storage.local.remove('bridge');return;}
  ack=null;const {command}=await response.json();
  if(command){const current=await chrome.storage.local.get('target');const source=current.target===target&&command.generation===generation?sources.find(([,frame])=>frame.actions.includes(command.action)):null;
   if(!source)ack={id:command.id,success:false};
   else try{ack=await chrome.tabs.sendMessage(target,{kind:'execute',command,page:source[1].page},{frameId:source[0]});}catch{ack={id:command.id,success:false};}
  }
 }finally{busy=false;}
}
chrome.tabs.onRemoved.addListener(async id=>{const {target}=await chrome.storage.local.get('target');if(target===id){await chrome.storage.local.remove('target');frames.clear();}});
chrome.tabs.onUpdated.addListener((id,change)=>{if(change.status==='loading')chrome.storage.local.get('target').then(({target})=>{if(target===id){frames.clear();ack=null;generation=crypto.randomUUID().replaceAll('-','');}});});
chrome.storage.onChanged.addListener((changes,area)=>{if(area==='local'&&changes.target){frames.clear();ack=null;generation=crypto.randomUUID().replaceAll('-','');}});
chrome.webNavigation.onDOMContentLoaded.addListener(async details=>{
 const {target}=await chrome.storage.local.get('target');
 if(target!==details.tabId||!/^https?:/.test(details.url))return;
 await chrome.scripting.executeScript({target:{tabId:target,frameIds:[details.frameId]},files:['player.js','content.js']}).catch(()=>{});
});
