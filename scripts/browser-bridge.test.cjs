const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const {webcrypto}=require('node:crypto');
const listeners={};let stored={bridge:{port:8765,token:'test'},target:7},posts=[],sent=[],next=null,beforeResponse=null;
let responseStatus=200;
let clockOffset=0,inspections=0;const realNow=Date.now.bind(Date);class Clock extends Date{static now(){return realNow()+clockOffset;}}
const event=name=>({addListener(fn){listeners[name]=fn;}});
let windowState='maximized',windowUpdates=[];
let scripts=[],blockScripts=false;const session={};
const tabData=new Map([[7,{id:7,windowId:41,url:'https://video.test/watch?token=PRIVATE',title:'Test video'}],[9,{id:9,windowId:42,url:'https://other.test/episode',title:'Other video',active:true}],[5,{id:5,windowId:41,url:'https://music.test/',title:'Music',audible:true}],[11,{id:11,windowId:41,url:'https://secret.test/',title:'PRIVATE-INCOGNITO',incognito:true}],[12,{id:12,windowId:41,url:'chrome://settings',title:'Settings'}]]);
const chrome={runtime:{id:'test-extension',onMessage:event('message'),onStartup:event('startup'),onInstalled:event('installed')},alarms:{onAlarm:event('alarm'),get:async()=>({}),create:async()=>{},clear:async()=>{}},permissions:{contains:async()=>true},storage:{session:{get:async key=>({[key]:session[key]}),set:async value=>Object.assign(session,value),remove:async key=>{delete session[key];}},local:{
 get:async key=>typeof key==='string'?{[key]:stored[key]}:{...stored},
 set:async values=>{const changes={};for(const[key,newValue]of Object.entries(values)){changes[key]={oldValue:stored[key],newValue};stored[key]=newValue;}listeners.changed(changes,'local');},
 remove:async keys=>{const changes={};for(const key of Array.isArray(keys)?keys:[keys]){changes[key]={oldValue:stored[key]};delete stored[key];}listeners.changed(changes,'local');}
},onChanged:event('changed')},windows:{get:async id=>({id,state:windowState}),update:async(id,options)=>{windowUpdates.push({id,...options});if(options.state)windowState=options.state;return{id,state:windowState};}},tabs:{query:async()=>[...tabData.values()],get:async id=>{if(!tabData.has(id))throw Error('Closed');return tabData.get(id);},update:async()=>{},onRemoved:event('removed'),onUpdated:event('updated'),sendMessage:async(tab,message,options)=>{if(message.kind==='probe')return{title:'Selected player',actions:['pause','set-volume','mute-player'],volume:65,muted:false,page:'selected-page',area:500};if(message.kind==='inspect'){inspections++;return{title:'Background video',actions:['play'],page:message.page,area:500};}sent.push({tab,message,options});return{id:message.command.id,success:true};}},webNavigation:{getAllFrames:async()=>[{frameId:0,url:'https://video.test/'},{frameId:99,url:'https://blocked.test/'}],onDOMContentLoaded:event('navigation')},scripting:{executeScript:async options=>{scripts.push(options);if(blockScripts||options.target.frameIds?.includes(99))throw Error("Frame access denied");}}};
const context=vm.createContext({chrome,crypto:webcrypto,Date:Clock,URL,setInterval:()=>{},clearInterval:()=>{},setTimeout,clearTimeout,AbortSignal,fetch:async(url,options)=>{
 const body=JSON.parse(options.body);posts.push(body);if(beforeResponse){const run=beforeResponse;beforeResponse=null;await run();}
 const command=next?{...next,generation:body.generation}:null;next=null;const status=responseStatus;responseStatus=200;return{ok:status===200,status,json:async()=>({command})};
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
 await new Promise(setImmediate);const count=posts.length;await report();assert.equal(posts.length,count,'Disconnected frame cannot report');assert.deepEqual(posts.at(-1).actions,[]);assert.equal(posts.at(-1).title,'');
 let handled=false;listeners.message({kind:'stop'},{id:'another-extension'},()=>{handled=true;});assert.equal(handled,false,'Foreign extension messages are ignored');
 const pulse=async()=>{await new Promise(setImmediate);await vm.runInContext('sync()',context);await new Promise(setImmediate);};
 await pulse();assert.equal(posts.at(-1).tabs,null,'Tab sharing defaults off');
 stored.tabSharing=true;await pulse();
 assert.deepEqual(posts.at(-1).tabs.map(tab=>tab.id),[5,9,7],'Audible tabs come first and internal/incognito pages are excluded');
 assert.ok(!JSON.stringify(posts.at(-1)).includes('PRIVATE'),'URLs and incognito titles never reach host');
 next={id:'choose-nine',action:'select-tab',tabId:9};await pulse();assert.equal(stored.target,9);assert.equal(scripts.at(-1).target.tabId,9);
 await pulse();assert.equal(posts.at(-1).selectedTab,9);assert.equal(posts.at(-1).commandId,'choose-nine');assert.equal(posts.at(-1).success,true,'Selection acknowledgement survives generation change');
 assert.ok(posts.at(-1).actions.includes('pause'),'Selection discovers playable controls immediately despite an inaccessible iframe');
 blockScripts=true;next={id:'blocked-selection',action:'select-tab',tabId:7};await pulse();await pulse();assert.equal(posts.at(-1).success,false,'Injection failure is never acknowledged as a successful connection');assert.equal(stored.target,undefined);blockScripts=false;
 next={id:'rechoose-nine',action:'select-tab',tabId:9};await pulse();await pulse();
assert.equal(posts.at(-1).resumeTab,9);assert.equal(posts.at(-1).volume,65);assert.equal(posts.at(-1).muted,false);
 next={id:'video-volume',action:'set-volume',tabId:9,volume:32};await pulse();assert.equal(sent.at(-1).message.command.volume,32);assert.equal(sent.at(-1).tab,9);
 await pulse();const beforeWrongVolume=sent.length;next={id:'wrong-video-volume',action:'set-volume',tabId:7,volume:20};await pulse();await pulse();assert.equal(sent.length,beforeWrongVolume,'Volume cannot drift to another selected tab');assert.equal(posts.at(-1).success,false);
 next={id:'focus-seven',action:'focus-tab',tabId:7};await pulse();assert.equal(windowUpdates.at(-1).id,41);assert.equal(stored.target,9,'Focus does not change the remote selection');
 await pulse();next={id:'detach',action:'detach-tab'};await pulse();await pulse();assert.equal(posts.at(-1).selectedTab,null);assert.equal(posts.at(-1).success,true);assert.ok(posts.at(-1).tabs.length,'Detaching keeps tab picker available');assert.equal(posts.at(-1).resumeTab,9,'Detaching preserves the previous open player');
 tabData.delete(9);next={id:'closed-nine',action:'select-tab',tabId:9};await pulse();await pulse();assert.equal(posts.at(-1).success,false,'A closed tab cannot be selected');assert.equal(posts.at(-1).resumeTab,null,'Closed previous tab cannot be offered for resume');
 stored.tabSharing=false;next={id:'no-consent',action:'select-tab',tabId:7};await pulse();await pulse();assert.equal(posts.at(-1).tabs,null);assert.equal(posts.at(-1).success,false,'Disabling sharing blocks tab selection');
 stored.tabSharing=true;stored.target=11;await pulse();assert.equal(posts.at(-1).title,'');assert.equal(posts.at(-1).selectedTab,null);assert.ok(!JSON.stringify(posts.at(-1)).includes('PRIVATE-INCOGNITO'),'Even a stale incognito target cannot disclose its title');
 responseStatus=401;beforeResponse=async()=>{await chrome.storage.local.set({bridge:{port:8765,token:'replacement'}});};await pulse();assert.equal(stored.bridge.token,'replacement','Late rejection cannot remove a newly paired credential');
 clockOffset+=20000;stored.target=7;await report(7,['play']);clockOffset+=5000;await pulse();assert.equal(inspections,1);assert.ok(posts.at(-1).actions.includes('play'),'Background player remains available without its own timer heartbeat');
 await listeners.startup();await new Promise(setImmediate);assert.equal(stored.target,undefined,'Browser restart clears non-persistent tab ids');
 console.log('Browser routing passed: media, fullscreen, tab catalog privacy, selection, acknowledgement across navigation, focus, detach, closed tabs and consent.');
})().catch(error=>{console.error(error);process.exitCode=1;});
