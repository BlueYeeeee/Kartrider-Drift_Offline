'use strict';
// Pure-local mode: explicitly unavailable telemetry, not fake delivery success.
// Only the identified Analytics DLL; account SDK and anti-cheat are untouched.
const retained=[];
let configured=false;
let settle=()=>({configured:false});
let status=()=>({configured:false});
let moduleBase=null;
rpc.exports={settle:()=>settle(),status:()=>status()};
function hex(p,n){return Array.from(new Uint8Array(p.readByteArray(n))).map(v=>v.toString(16).padStart(2,'0')).join('');}
const observer=Process.attachModuleObserver({onAdded(m){
  if(m.name.toLowerCase()!=='nexonanalytics64.dll'||configured)return;
  const init=m.getExportByName('initializeClientModule');
  const stop=m.getExportByName('finalizeModule');
  if(hex(init,16)!=='488bc4574883ec7048c740a8feffffff' ||
     hex(stop,16)!=='0fb6d1488b0d0ee64800e9d1280100cc'){
    send({kind:'offline_analytics_version_mismatch'});return;
  }
  const unavailable=new NativeCallback(function(){
    send({kind:'offline_analytics_initialization_disabled'});return 0;
  },'bool',['pointer','pointer','pointer']);
  retained.push(unavailable);
  // The DLL can unload and reuse its base. Discard the stale interception
  // record before replacing the newly mapped, version-checked entry point.
  Interceptor.revert(init);
  Interceptor.flush();
  Interceptor.replace(init,unavailable);
  Interceptor.flush();
  configured=true;
  moduleBase=m.base.toString();
  status=()=>{
    const instance=m.base.add(0x5c18a8).readPointer();
    return {configured:true,moduleBase,initBytes:hex(init,16),
      initialized:instance.isNull()?null:instance.add(0x289).readU8()};
  };
  send({kind:'offline_analytics_configured',...status()});
  settle=()=>{
    const instance=m.base.add(0x5c18a8).readPointer();
    const before=instance.isNull()?null:instance.add(0x289).readU8();
    if(!instance.isNull() && instance.add(0x289).readU8()!==0){
      // Native finalizer first clears initialized then shuts its workers down.
      new NativeFunction(stop,'void',['bool'])(0);
      send({kind:'offline_analytics_finalized',initialized:instance.add(0x289).readU8()});
    }
    return {...status(),before};
  };
  setImmediate(settle);
},onRemoved(m){
  if(m.name.toLowerCase()!=='nexonanalytics64.dll'||m.base.toString()!==moduleBase)return;
  send({kind:'offline_analytics_module_removed',moduleBase});
  configured=false;
  moduleBase=null;
  status=()=>({configured:false});
  settle=()=>status();
}});
