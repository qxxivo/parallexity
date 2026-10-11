const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const reports=[],executions=[];let listener,inspections=0,intervals=0;
const chrome={runtime:{id:'extension-test',onMessage:{addListener(fn){listener=fn;}},sendMessage:async message=>{if(message.kind==='is-target')return true;reports.push(message.report);return{done:true};}}};
const context=vm.createContext({chrome,crypto:require('node:crypto').webcrypto,setInterval:()=>++intervals,clearInterval:()=>{},ParallexityPlayer:{inspect:()=>{inspections++;return{title:'Player',actions:['pause'],area:500};},execute:async action=>{executions.push(action);return true;}}});
const source=fs.readFileSync(require.resolve('../browser-extension/content.js'),'utf8');
const settle=async()=>{for(let i=0;i<20;i++)await Promise.resolve();};
function message(body,sender={id:'extension-test'}){return new Promise(resolve=>{const keep=listener(body,sender,resolve);if(!keep&&sender.id!=='extension-test')resolve(undefined);});}
(async()=>{
 vm.runInContext(source,context);await settle();assert.equal(reports.length,1);const page=reports[0].page;
 const before=inspections;assert.equal(await message({kind:'inspect',page:'old-page'}),null);assert.equal(inspections,before,'Old page cannot be inspected');
 assert.equal((await message({kind:'inspect',page})).title,'Player');
 assert.equal(await message({kind:'inspect',page},{id:'foreign-extension'}),undefined);
 assert.equal((await message({kind:'execute',page:'old-page',command:{id:'old',action:'pause'}})).success,false);assert.equal(executions.length,0);
 assert.equal((await message({kind:'execute',page,command:{id:'one',action:'pause'}})).success,true);
 assert.equal((await message({kind:'execute',page,command:{id:'one',action:'pause'}})).success,false);assert.deepEqual(executions,['pause'],'A command cannot execute twice');
 vm.runInContext(source,context);await settle();assert.equal(intervals,1);assert.equal(reports.length,2,'Re-selecting immediately refreshes even a throttled background frame');
 console.log('Content protocol passed: page identity, sender identity, inspection, duplicate commands and immediate re-selection heartbeat.');
})().catch(error=>{console.error(error);process.exitCode=1;});
