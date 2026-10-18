// Read-only endpoint/caller metadata. No authentication or application payloads.
const attached = new Set();
const sockets = new Map();
function address(p) {
    if (p.isNull()) return null;
    const family = p.readU16();
    const port = p.add(2).readU8() * 256 + p.add(3).readU8();
    if (family === 2) return {family, port, ip: [4,5,6,7].map(i => p.add(i).readU8()).join('.')};
    if (family === 23) return {family, port, ip: Array.from(new Uint8Array(p.add(8).readByteArray(16))).map(x => x.toString(16).padStart(2,'0')).join('')};
    return {family};
}
function caller(p) {
    const m = Process.findModuleByAddress(p);
    return m ? m.name + '+' + p.sub(m.base) : 'unknown';
}
function install(m, name, index) {
    const p = m.findExportByName(name);
    if (!p || attached.has(p.toString())) return;
    attached.add(p.toString());
    Interceptor.attach(p, {onEnter(a) {
        try {
            this.started = Date.now();
            this.event = {api:name, caller:caller(this.returnAddress)};
            const event = this.event;
            if (name.startsWith('Icmp')) {
                const destinationIndex = name === 'IcmpSendEcho' ? 1 : name === 'IcmpSendEcho2' ? 4 : 5;
                if (name === 'Icmp6SendEcho2') event.endpoint = address(a[destinationIndex]);
                else {
                    const ip = a[destinationIndex].toUInt32();
                    event.destination = [0,8,16,24].map(shift => (ip >>> shift) & 255).join('.');
                    this.reply = a[name === 'IcmpSendEcho' ? 5 : name === 'IcmpSendEcho2' ? 8 : 9];
                }
            } else {
                event.socket = a[0].toString();
                event.socketType = sockets.get(event.socket);
                if (index !== null) event.endpoint = address(a[index]);
                if (name === 'recvfrom') this.receiveAddress = a[4];
                if (name.startsWith('WSA') && /Send|Recv/.test(name)) {
                    this.transferred = a[3];
                    this.overlapped = !a[name.endsWith('To') || name.endsWith('From') ? 7 : 5].isNull();
                    if (name === 'WSARecvFrom') this.receiveAddress = a[5];
                }
            }
        } catch (_) { send({api:name, parseError:true}); }
    }, onLeave(r) {
        if (!this.event) return;
        const event = this.event;
        try {
            event.result = r.toInt32();
            event.callDurationMs = Date.now() - this.started;
            if (name.startsWith('Icmp')) {
                if (event.result > 0 && this.reply && !this.reply.isNull()) {
                    event.replyStatus = this.reply.add(4).readU32();
                    event.roundTripMs = this.reply.add(8).readU32();
                } else if (event.result === 0) event.winError = this.lastError;
            } else if (event.result < 0) {
                event.winError = this.lastError;
                if (this.overlapped && event.winError === 997) event.completionPending = true;
            } else {
                if (this.receiveAddress) event.endpoint = address(this.receiveAddress);
                if (this.transferred && !this.transferred.isNull()) event.bytesTransferred = this.transferred.readU32();
                else if (['send','recv','sendto','recvfrom'].includes(name)) event.bytesTransferred = event.result;
            }
        } catch (_) { event.parseError = true; }
        send(event);
    }});
    send({api:'hook_ready', name});
}
const moduleObserver = Process.attachModuleObserver({onAdded(m) {
    if (m.name.toLowerCase() === 'bcrypt.dll') {
        const p = m.findExportByName('BCryptEncrypt');
        if (p && !attached.has(p.toString())) {
            attached.add(p.toString());
            Interceptor.attach(p,{onEnter(a){
                try {
                    const n=a[2].toUInt32();
                    if(n<2 || n>262144)return;
                    const text=a[1].readUtf8String(n);
                    if(text[0]!=='{' || !text.includes('PqAwsLatencyInfo'))return;
                    const packet=JSON.parse(text.slice(0,text.lastIndexOf('}')+1));
                    if(packet._type!=='PqAwsLatencyInfo' || !Array.isArray(packet.awsLatencyInfoList))return;
                    send({api:'PqAwsLatencyInfo',regions:packet.awsLatencyInfoList.map(x=>({region:x.region,pingMs:x.pingMs}))});
                } catch (_) {}
            }});
            send({api:'hook_ready',name:'latency_report'});
        }
    }
    if (m.name.toLowerCase() === 'ws2_32.dll') {
        for (const name of ['socket','WSASocketW','WSASocketA']) {
            const p = m.findExportByName(name);
            if (p && !attached.has(p.toString())) {
                attached.add(p.toString());
                Interceptor.attach(p,{onEnter(a){this.type=a[1].toInt32();},onLeave(r){sockets.set(r.toString(),this.type);}});
            }
        }
        const close = m.findExportByName('closesocket');
        if (close && !attached.has(close.toString())) {
            attached.add(close.toString());
            Interceptor.attach(close,{onEnter(a){this.socket=a[0].toString();},onLeave(r){if(r.toInt32()===0)sockets.delete(this.socket);}});
        }
        for (const [name,index] of [['connect',1],['WSAConnect',1],['sendto',4],['WSASendTo',5],['recvfrom',null],['WSARecvFrom',null],['send',null],['recv',null],['WSASend',null],['WSARecv',null]]) install(m,name,index);
    }
    if (m.name.toLowerCase() === 'iphlpapi.dll') {
        for (const name of ['IcmpSendEcho','IcmpSendEcho2','IcmpSendEcho2Ex','Icmp6SendEcho2']) install(m,name,null);
    }
}});
send({api:'observer_ready'});
