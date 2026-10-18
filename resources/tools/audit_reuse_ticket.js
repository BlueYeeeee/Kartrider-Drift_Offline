'use strict';
// Opt-in identity source. Fixed-local mode initializes only this local adapter;
// it never searches for or loads an installed Nexon launcher/runtime.
// No credential content is emitted.
// This changes the in-process SDK ticket source; it is not an observation-only probe.
let ticketBuffer=null;
let observer=null;
const hooked=new Set();
const retained=[];
let localMode=false;
rpc.exports={status(){return {localMode,externalRuntimeLoaded:Process.findModuleByName('nexon_x64.dll')!==null};},provide(value) {
  if(ticketBuffer!==null) throw new Error('Ticket already provided');
  if(typeof value!=='string' || value.length===0 || value.length>8192 || value.includes('\0'))
    throw new Error('Invalid captured ticket shape');
  ticketBuffer=Memory.allocUtf8String(value);
  localMode=value==='KartDriftLocalOfflineIdentity00001';
  observer=Process.attachModuleObserver({onAdded(m) {
    if(m.name.toLowerCase()!=='nexon_api_x64.dll' || hooked.has(m.base.toString())) return;
    hooked.add(m.base.toString());
    if(localMode){
      const init=m.getExportByName('nxapi_init');
      const prefix=Array.from(new Uint8Array(init.readByteArray(16)),v=>v.toString(16).padStart(2,'0')).join('');
      if(prefix!=='40574883ec20488bf94885c9750833c0')throw new Error('Local SDK binary version mismatch');
      const initialize=new NativeCallback(function(){send({kind:'local_sdk_initialized',externalRuntime:false});return 1;},'bool',['pointer']);
      retained.push(initialize);Interceptor.replace(init,initialize);
      for(const name of ['nxapi_get_product_ticket','nxcapi_get_product_ticket']){
        const get=new NativeCallback(function(){send({kind:'local_ticket_supplied'});return ticketBuffer;},'pointer',[]);
        retained.push(get);Interceptor.replace(m.getExportByName(name),get);
      }
      Interceptor.flush();send({kind:'local_sdk_ready',externalRuntime:false});return;
    }
    Interceptor.attach(m.getExportByName('nxapi_init'), {
      onLeave(ret) { send({kind:'real_sdk_init_return', result:ret.toInt32()}); }
    });
    Interceptor.attach(m.getExportByName('nxapi_get_product_ticket'), {
      onLeave(ret) {
        let originalEmpty=null;
        try { originalEmpty=ret.isNull() || ret.readU8()===0; } catch(e) {}
        ret.replace(ticketBuffer);
        send({kind:'ticket_value_supplied', original_empty:originalEmpty});
      }
    });
    send({kind:'ticket_replay_hook_ready'});
  }});
  send({kind:'ticket_value_loaded',length:value.length});
}};
