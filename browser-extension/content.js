(() => {
if(globalThis.parallexityContent){globalThis.parallexityStart();return;}globalThis.parallexityContent=true;
let running=false,executed=new Set(),page=crypto.randomUUID(),timer=null;
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
 if(message.kind==='probe'&&sender.id===chrome.runtime.id){reply({...ParallexityPlayer.inspect(),page});return;}
 if(message.kind==='inspect'&&sender.id===chrome.runtime.id){reply(message.page===page?{...ParallexityPlayer.inspect(),page}:null);return;}
 if(message.kind!=='execute'||sender.id!==chrome.runtime.id)return;
 if(message.page!==page){reply({id:message.command.id,success:false});return;}
 if(executed.has(message.command.id)){reply({id:message.command.id,success:false});return;}
 executed.add(message.command.id);if(executed.size>32)executed.delete(executed.values().next().value);
 ParallexityPlayer.execute(message.command.action).then(success=>reply({id:message.command.id,success}));return true;
});
async function heartbeat(){if(running)return;running=true;try{const active=await chrome.runtime.sendMessage({kind:'is-target'});if(!active){clearInterval(timer);timer=null;return;}await chrome.runtime.sendMessage({kind:'report',report:{...ParallexityPlayer.inspect(),page}});}catch{clearInterval(timer);timer=null;}finally{running=false;}}
globalThis.parallexityStart=()=>{if(timer===null)timer=setInterval(heartbeat,1000);void heartbeat();};globalThis.parallexityStart();
})();
