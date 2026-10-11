const status = document.getElementById('status');
const enable = document.getElementById('enable');
const share = document.getElementById('share');
share.disabled=true;
enable.disabled=true;
chrome.storage.local.get(['bridge','tabSharing']).then(({bridge,tabSharing})=>{enable.disabled=share.disabled=!bridge;if(tabSharing)status.textContent='Выбор вкладок с телефона включён.';});
async function perform(button, work) {
 button.disabled=true;
 try {status.textContent=await work();} catch(error){status.textContent=error.message||'Не удалось подключиться.';} finally{button.disabled=false;}
}
document.getElementById('pair').onclick=function(){perform(this,async()=>{
 const match=document.getElementById('code').value.trim().match(/^(\d{1,5}):([A-Fa-f0-9]{32})$/);
 if(!match||Number(match[1])<1||Number(match[1])>65535)throw new Error('Скопируй полный код из приложения ПК.');
 const port=Number(match[1]);const reply=await fetch(`http://127.0.0.1:${port}/browser/pair`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token:match[2].toUpperCase(),name:'Browser'}),signal:AbortSignal.timeout(5000)});
 const data=await reply.json();if(!reply.ok)throw new Error(data.error||'Пульт недоступен.');
 await chrome.storage.local.set({bridge:{port,token:data.token}});enable.disabled=share.disabled=false;document.getElementById('code').value='';await chrome.runtime.sendMessage({kind:'wake'});return 'ПК подключён. Включи выбор вкладок с телефона.';
});};
document.getElementById('enable').onclick=function(){perform(this,async()=>{
 const granted=await chrome.permissions.request({origins:['http://*/*','https://*/*']});if(!granted)throw new Error('Разрешение не выдано.');
 const {bridge}=await chrome.storage.local.get('bridge');if(!bridge)throw new Error('Сначала подключи ПК.');
 const [tab]=await chrome.tabs.query({active:true,currentWindow:true});if(!tab?.id||!/^https?:/.test(tab.url||''))throw new Error('Открой сайт с видео.');
 await chrome.storage.local.set({target:tab.id});
 await chrome.scripting.executeScript({target:{tabId:tab.id,allFrames:true},files:['player.js','content.js']});
 return 'Вкладка подключена. Доступные кнопки появятся на телефоне.';
});};
share.onclick=function(){perform(this,async()=>{
 const granted=await chrome.permissions.request({origins:['http://*/*','https://*/*']});if(!granted)throw new Error('Разрешение не выдано.');
 const{bridge}=await chrome.storage.local.get('bridge');if(!bridge)throw new Error('Сначала подключи ПК.');
 await chrome.storage.local.set({tabSharing:true});await chrome.runtime.sendMessage({kind:'wake'});return 'Готово. Выбирай вкладки в разделе «Медиа» на телефоне.';
});};
document.getElementById('stop').onclick=function(){perform(this,async()=>{await chrome.runtime.sendMessage({kind:'stop'});return 'Выбор вкладок и управление отключены.';});};
