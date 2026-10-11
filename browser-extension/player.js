(() => {
'use strict';
function visible(node) {
 if(!node||node.disabled||node.getAttribute('aria-disabled')==='true')return false;
 const box=node.getBoundingClientRect(),style=getComputedStyle(node);
 return box.width>3&&box.height>3&&style.display!=='none'&&style.visibility!=='hidden'&&Number(style.opacity)!==0;
}
function findVideo() {return [...document.querySelectorAll('video')].filter(visible).sort((a,b)=>b.getBoundingClientRect().width*b.getBoundingClientRect().height-a.getBoundingClientRect().width*a.getBoundingClientRect().height)[0];}
const labels={
 'skip-intro':/^(?:пропустить (?:заставку|вступление|интро)|skip (?:intro|opening))\s*[.!]?$/i,
 'skip-ad':/^(?:пропустить рекламу|skip (?:ad|ads|advertisement))\s*[.!]?$/i,
 'next-episode':/^(?:следующая серия|следующий эпизод|next episode)\s*[.!]?$/i
};
function buttons() {
 const found={};
 for(const node of [...document.querySelectorAll('button,[role="button"],[class*="skip"],[class*="intro"]')].slice(0,1000)) {
  if(!visible(node))continue;
  const values=[node.innerText,node.getAttribute('aria-label'),node.getAttribute('title')].filter(Boolean).map(s=>s.replace(/\s+/g,' ').trim());
  for(const [action,pattern] of Object.entries(labels))if(!found[action]&&values.some(value=>value.length<100&&pattern.test(value)))found[action]=node;
 }
 const youtube=document.querySelector('.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button');
 if(visible(youtube)&&!youtube.querySelector('[class*="countdown"]'))found['skip-ad']=youtube;
 return found;
}
function inspect() {
 const video=findVideo(),found=buttons(),actions=Object.keys(found);
 if(video){actions.push(video.paused?'play':'pause');if(Number.isFinite(video.volume)&&typeof video.muted==='boolean')actions.push('set-volume',video.muted?'unmute-player':'mute-player');if(Number.isFinite(video.duration)&&video.duration>0)actions.push('seek-back','seek-forward');}
 return {title:(location.hostname+' · '+document.title).slice(0,140),actions,area:video?video.getBoundingClientRect().width*video.getBoundingClientRect().height:0,volume:video&&Number.isFinite(video.volume)?Math.round(video.volume*100):null,muted:video&&typeof video.muted==='boolean'?video.muted:null};
}
async function execute(action,volume) {
 const found=buttons();if(found[action]){found[action].click();return true;}
 const video=findVideo();if(!video)return false;
 try {
  if(action==='set-volume'){if(!Number.isInteger(volume)||volume<0||volume>100)return false;video.volume=volume/100;return Math.abs(video.volume-volume/100)<.005;}
  if(action==='mute-player'||action==='unmute-player'){video.muted=action==='mute-player';return video.muted===(action==='mute-player');}
  if(action==='play'){await video.play();return true;}
  if(action==='pause'){video.pause();return true;}
  if((action==='seek-back'||action==='seek-forward')&&Number.isFinite(video.duration)&&video.duration>0){video.currentTime=Math.max(0,Math.min(video.duration,video.currentTime+(action==='seek-back'?-10:10)));return true;}
 }catch{}
 return false;
}
const api={inspect,execute};if(typeof module!=='undefined'&&module.exports)module.exports=api;else globalThis.ParallexityPlayer=api;
})();
