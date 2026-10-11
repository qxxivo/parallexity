const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const {webcrypto}=require('node:crypto');
const listeners={};let stored={bridge:{port:8765,token:'test'},target:7},posts=[],sent=[],next=null,beforeResponse=null;
const event=name=>({addListener(fn){listeners[name]=fn;}});
let windowState='maximized',windowUpdates=[];
const chrome={runtime:{id:'test-extension',onMessage:event('message')},storage:{local:{
 get:async key=>typeof key==='string'?{[key]:stored[key]}:{...stored},
 set:async values=>Object.assign(stored,values),
 remove:async key=>{const oldValue=stored[key];delete stored[key];listeners.changed({[key]:{oldValue}},'local');}
},onChanged:event('changed')},windows:{get:async id=>({id,state:windowState}),update:async(id,options)=>{windowUpdates.push({id,...options});windowState=options.state;return{id,state:windowState};}},tabs:{get:async id=>({id,windowId:41}),update:async()=>{},onRemoved:event('removed'),onUpdated:event('updated'),sendMessage:async(tab,message,options)=>{sent.push({tab,message,options});return{id:message.command.id,success:true};}},webNavigation:{onDOMContentLoaded:event('navigation')},scripting:{executeScript:async()=>{}}};
const context=vm.createContext({chrome,crypto:webcrypto,Date,AbortSignal,fetch:async(url,options)=>{
 const body=JSON.parse(options.body);posts.push(body);if(beforeResponse){const run=beforeResponse;beforeResponse=null;await run();}
 const command=next?{...next,generation:body.generation}:null;next=null;return{ok:true,json:async()=>({command})};
}});
vm.runInContext(fs.readFileSync(require.resolve('../browser-extension/background.js'),'utf8'),context);
const sender=(tab=7,frameId=3)=>({id:'test-extension',tab:{id:tab,windowId:41},frameId});
const report=(tab=7,actions=['skip-intro'])=>new Promise(resolve=>listeners.message({kind:'report',report:{title:'Test player',actions,page:'page-one',area:500}},sender(tab),resolve));
(async()=>{
 await report(8);assert.equal(posts.length,0,'Only the selected tab may report');
 next={id:'intro-one',action:'skip-intro'};await report();
 assert.equal(sent.length,1);assert.equal(sent[0].tab,7);assert.equal(sent[0].options.frameId,3);assert.equal(sent[0].message.page,'page-one');
 await report();assert.equal(posts.at(-1).commandId,'intro-one');assert.equal(posts.at(-1).success,true,'Execution acknowledgement reaches host');
 next={id:'missing-ad',action:'skip-ad'};await report();assert.equal(sent.length,1,'Unsupported actions are not routed');
 await report();assert.equal(posts.at(-1).success,false);
 assert.ok(posts.at(-1).actions.includes('fullscreen'));
 next={id:'full-window',action:'fullscreen'};await report();assert.equal(windowState,'fullscreen');assert.equal(windowUpdates.at(-1).id,41);assert.equal(windowUpdates.at(-1).focused,true);
 await report();assert.ok(posts.at(-1).actions.includes('exit-fullscreen'));assert.equal(posts.at(-1).success,true);
 next={id:'restore-window',action:'exit-fullscreen'};await report();assert.equal(windowState,'maximized','Exiting restores the previous maximized state');
 assert.equal(sent.length,1,'Fullscreen uses the window API without sending keys to a page');
 next={id:'stale-fullscreen',action:'fullscreen'};beforeResponse=async()=>{listeners.updated(7,{status:'loading'});await Promise.resolve();};
 await report();assert.equal(windowUpdates.length,2,'Navigation prevents stale fullscreen changes');
 next={id:'stale-page',action:'skip-intro'};beforeResponse=async()=>{listeners.updated(7,{status:'loading'});await Promise.resolve();};
 await report();assert.equal(sent.length,1,'Navigation cancels commands from the old generation');
 next={id:'changed-tab',action:'skip-intro'};beforeResponse=async()=>{stored.target=9;listeners.changed({target:{oldValue:7,newValue:9}},'local');};
 await report();assert.equal(sent.length,1,'Changing selected tab prevents an in-flight command');
 await new Promise(resolve=>listeners.message({kind:'stop'},sender(),resolve));assert.equal(stored.target,undefined);
 const count=posts.length;await report();assert.equal(posts.length,count,'Stop disables reports');
 let handled=false;listeners.message({kind:'stop'},{id:'another-extension'},()=>{handled=true;});assert.equal(handled,false,'Foreign extension messages are ignored');
 console.log('Browser routing passed: selected tab, frame and page, acknowledgement, absent buttons, navigation, target changes, stop and sender identity.');
})().catch(error=>{console.error(error);process.exitCode=1;});
