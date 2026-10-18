'use strict';
// Local per-car restoration intentionally changes these slots. Native
// 0x1E27E40 checks pending items against the applied preset, not inventory.
function restoredVehicleSlots(previousKart, request, response) {
  if(request?._type!=='PqItemPresetOnUpdate'||response?._type!=='PrItemPresetOnUpdate' ||
     request.reqId!==response.reqId||response.result?.code!==0)return [];
  const before=request.itemPreset?.itemMap,after=response.itemPreset?.itemMap;
  if(!before||!after||!previousKart||!before.Kart||before.Kart===previousKart||after.Kart!==before.Kart)return [];
  const allowed=new Set(['PartBack','PartFront','PartSide','PartHandle','PartPlate',
    'PartWheel','KartAura','KartBooster','SkidMark']);
  const changed=Array.from(new Set([...Object.keys(before),...Object.keys(after)]))
    .filter(k=>before[k]!==after[k]);
  return changed.length&&changed.every(k=>allowed.has(k))?changed:[];
}
if(typeof module!=='undefined')module.exports={restoredVehicleSlots};
else {
  const game=Process.getModuleByName('KartDrift-Win64-Shipping.exe');
  const address=game.base.add(0x1e27e40),prefix='488974241048897c2418554154415541';
  let currentKart=typeof initialKart==='undefined'?null:initialKart,armed=null,installed=false;
  const pending=new Map();
  function packet(p,n,direction) {try{
    if(p.isNull()||n<2||n>4*1024*1024||p.readU8()!==123)return;
    const text=p.readUtf8String(n),value=JSON.parse(text.slice(0,text.lastIndexOf('}')+1));
    if(direction==='request'&&value._type==='PqItemPresetOnUpdate'){
      armed=null;pending.clear();pending.set(value.reqId,{request:value,previousKart:currentKart});
    }
    if(direction!=='response')return;
    if(value._type==='PrLogin'){
      currentKart=value.itemPresetOn?.itemMap?.Kart||null;
      installNative();
    }
    if(value._type!=='PrItemPresetOnUpdate')return;
    const saved=pending.get(value.reqId);pending.delete(value.reqId);
    if(saved){
      const slots=restoredVehicleSlots(saved.previousKart,saved.request,value);
      // Keep only vehicle configuration for later comparison, never whole
      // login/transport packets or account authentication fields.
      const project=preset=>({itemMap:Object.fromEntries(Object.entries(preset?.itemMap||{})
        .filter(([k])=>['Kart','PartBack','PartFront','PartSide','PartHandle','PartPlate',
          'PartWheel','KartAura','KartBooster','SkidMark'].includes(k))),livery:preset?.livery});
      send({kind:'local_vehicle_restore_comparison',reqId:value.reqId,result:value.result?.code,
        request:project(saved.request.itemPreset),response:project(value.itemPreset),restoredSlots:slots});
      armed=slots.length?{reqId:value.reqId,slots,expires:Date.now()+2000}:null;
    }
    if(value.result?.code===0)currentKart=value.itemPreset?.itemMap?.Kart||currentKart;
  }catch(e){armed=null;}}
  function installNative(){
    if(installed)return;
    const bytes=Array.from(new Uint8Array(address.readByteArray(16)))
      .map(b=>b.toString(16).padStart(2,'0')).join('');
    if(bytes!==prefix){send({kind:'local_vehicle_restore_version_mismatch'});return;}
    Interceptor.attach(address,{
    onEnter(){
      this.restore=this.returnAddress.equals(game.base.add(0x1fd4a79))&&
        armed&&Date.now()<=armed.expires?armed:null;
    },
    onLeave(r){
      if(!this.restore)return;
      armed=null;
      if((r.toUInt32()&255)===0){
        r.replace(1);
        send({kind:'local_vehicle_restore_confirmed',reqId:this.restore.reqId,slots:this.restore.slots});
      }
    }
    });
    installed=true;
    send({kind:'local_vehicle_restore_ready'});
  }
  Process.attachModuleObserver({onAdded(m){
    if(m.name.toLowerCase()!=='bcrypt.dll')return;
    Interceptor.attach(m.getExportByName('BCryptEncrypt'),{
      onEnter(a){packet(a[1],a[2].toUInt32(),'request');}
    });
    Interceptor.attach(m.getExportByName('BCryptDecrypt'),{
      onEnter(a){this.out=a[6];this.n=a[8];},
      onLeave(r){if(r.toInt32()===0)packet(this.out,this.n.readU32(),'response');}
    });
  }});
  // PrLogin is reached after native initialization. Avoid inspecting packed
  // startup bytes immediately when the process has just been created.
  if(currentKart!==null)installNative();
  else send({kind:'local_vehicle_restore_waiting_for_login'});
}
