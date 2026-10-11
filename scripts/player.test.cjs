const assert=require('node:assert/strict');
let candidates=[],video=null,youtube=null;
global.location={hostname:'player.test'};
global.document={title:'Test film',querySelectorAll:selector=>selector==='video'?(video?[video]:[]):candidates,querySelector:()=>youtube};
global.getComputedStyle=node=>({display:node.hidden?'none':'block',visibility:'visible',opacity:'1'});
function button(text,{disabled=false,hidden=false,countdown=false}={}) {return {innerText:text,disabled,hidden,clicked:0,getAttribute:()=>null,getBoundingClientRect:()=>({width:160,height:40}),querySelector:()=>countdown?{}:null,click(){this.clicked++;}};}
const player=require('../browser-extension/player.js');
(async()=>{
 const intro=button('Пропустить заставку'),ad=button('Skip ad',{disabled:true}),fake=button('Пропустить рекламу через 5 сек'),hidden=button('Next episode',{hidden:true});
 candidates=[intro,ad,fake,hidden];
 assert.deepEqual(player.inspect().actions,['skip-intro']);
 assert.equal(await player.execute('skip-intro'),true);assert.equal(intro.clicked,1);
 assert.equal(await player.execute('skip-ad'),false);assert.equal(ad.clicked,0);
 intro.hidden=true;assert.equal(await player.execute('skip-intro'),false,'Action rescans current visibility before click');
 const next=button('Следующая серия');candidates=[next];assert.equal(await player.execute('next-episode'),true);
 youtube=button('Skip',{countdown:true});assert.equal(player.inspect().actions.includes('skip-ad'),false);
 youtube=button('Skip');assert.equal(player.inspect().actions.includes('skip-ad'),true);assert.equal(await player.execute('skip-ad'),true);
 candidates=[];youtube=null;
 video={volume:.65,muted:false,paused:true,duration:100,currentTime:5,getAttribute:()=>null,getBoundingClientRect:()=>({width:800,height:450}),pause(){this.paused=true;},async play(){this.paused=false;}};
 assert.ok(player.inspect().actions.includes('play'));
 assert.equal(player.inspect().volume,65);assert.equal(player.inspect().muted,false);
 assert.equal(await player.execute('set-volume',32),true);assert.equal(video.volume,.32);
 assert.equal(await player.execute('set-volume',101),false);assert.equal(await player.execute('set-volume',-1),false);assert.equal(await player.execute('set-volume',NaN),false);assert.equal(video.volume,.32);
 assert.equal(await player.execute('mute-player'),true);assert.equal(player.inspect().muted,true);assert.ok(player.inspect().actions.includes('unmute-player'));
 await player.execute('set-volume',80);assert.equal(video.muted,true,'Changing volume preserves deliberate mute');
 await player.execute('unmute-player');assert.equal(video.muted,false);assert.equal(video.volume,.8);

 await player.execute('seek-back');assert.equal(video.currentTime,0);
 video.currentTime=98;await player.execute('seek-forward');assert.equal(video.currentTime,100);
 await player.execute('play');assert.equal(video.paused,false);await player.execute('pause');assert.equal(video.paused,true);
 video.duration=Infinity;assert.equal(player.inspect().actions.includes('seek-forward'),false,'Live video cannot seek outside a known duration');
 assert.equal(await player.execute('execute'),false,'Unknown action cannot trigger page code');
 console.log('Passed generic player: Russian/English labels, hidden/disabled/countdown buttons, rescanning, playback and bounded seeking.');
})().catch(error=>{console.error(error);process.exitCode=1;});
