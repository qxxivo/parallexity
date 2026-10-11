const $=id=>document.getElementById(id),origins=['http://*/*','https://*/*'];
async function render(){
 const{bridge,tabSharing}=await chrome.storage.local.get(['bridge','tabSharing']);
 const allowed=!!tabSharing&&await chrome.permissions.contains({origins});
 $('setup').hidden=!!bridge;$('ready').hidden=!bridge;$('share').hidden=allowed;
 $('connection').textContent=!bridge?'ПК ещё не подключён':allowed?'Подключено · выбор вкладок доступен':'ПК подключён · разреши управление ниже';
}
async function perform(button,work){button.disabled=true;try{$('status').textContent=await work();}catch(error){$('status').textContent=error.message||'Не удалось подключиться.';}finally{button.disabled=false;await render();}}
$('pair').onclick=function(){
 const match=$('code').value.trim().match(/^(\d{1,5}):([A-Fa-f0-9]{32})$/);
 if(!match||Number(match[1])<1||Number(match[1])>65535){$('status').textContent='Скопируй полный код из приложения ПК.';return;}
 const permission=chrome.permissions.request({origins});
 void perform(this,async()=>{
  if(!await permission)throw Error('Для управления плеером разреши доступ к сайтам.');
  const port=Number(match[1]),reply=await fetch(`http://127.0.0.1:${port}/browser/pair`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token:match[2].toUpperCase(),name:'Browser'}),signal:AbortSignal.timeout(5000)});
  const data=await reply.json();if(!reply.ok)throw Error(data.error||'Запусти приложение ПК и скопируй новый код.');
  await chrome.storage.local.set({bridge:{port,token:data.token},tabSharing:true});$('code').value='';await chrome.runtime.sendMessage({kind:'wake'});
  return 'Готово. Теперь выбирай вкладку на телефоне.';
 });
};
$('share').onclick=function(){const permission=chrome.permissions.request({origins});void perform(this,async()=>{if(!await permission)throw Error('Разрешение не выдано.');await chrome.storage.local.set({tabSharing:true});await chrome.runtime.sendMessage({kind:'wake'});return 'Готово. Выбирай вкладки на телефоне.';});};
$('enable').onclick=function(){const permission=chrome.permissions.request({origins});void perform(this,async()=>{
 if(!await permission)throw Error('Разрешение не выдано.');
 const[tab]=await chrome.tabs.query({active:true,currentWindow:true});if(!tab?.id||!/^https?:/.test(tab.url||''))throw Error('Открой обычный сайт с видео.');
 const reply=await chrome.runtime.sendMessage({kind:'connect-tab',tabId:tab.id});if(!reply?.done)throw Error('Не удалось подключить плеер. Перезагрузи страницу с видео и попробуй снова.');
 return 'Вкладка подключена. Открой «Медиа» на телефоне.';
});};
$('stop').onclick=function(){void perform(this,async()=>{await chrome.runtime.sendMessage({kind:'stop'});return 'Управление отключено. Его можно разрешить снова.';});};
void render();
