import {join} from "node:path";
import {pathToFileURL} from "node:url";
const module = await import(pathToFileURL(join(process.env.BENCHMARK_CANDIDATE_ROOT!,"src","emitter.ts" )).href);
const groups: {name:string;passed:boolean;checks:number;failures:string[]}[]=[];
function expect(value:boolean) {if(!value) throw new Error("behavior mismatch");}
function check(name:string,test:()=>void) {try {test();groups.push({name,passed:true,checks:1,failures:[]});} catch(error) {groups.push({name,passed:false,checks:1,failures:[String(error)]});}}
check("double_unsubscribe", () => { const e=new module.Emitter(); const hits:string[]=[]; const off=e.subscribe(()=>hits.push("a")); e.subscribe(()=>hits.push("b")); off(); off(); e.emit(1); expect(hits.join()==="b"); });
check("remove_during_emit", () => { const e=new module.Emitter(); const hits:string[]=[]; let off:()=>void; e.subscribe(()=>{hits.push("a");off();}); off=e.subscribe(()=>hits.push("b")); e.emit(1); expect(hits.join()==="a,b"); e.emit(2); expect(hits.join()==="a,b,a"); });
check("add_during_emit", () => { const e=new module.Emitter(); const hits:string[]=[]; let added=false; e.subscribe(()=>{hits.push("a");if(!added){added=true;e.subscribe(()=>hits.push("b"));}}); e.emit(1); expect(hits.join()==="a"); e.emit(2); expect(hits.join()==="a,a,b"); });
check("duplicate_registrations", () => { const e=new module.Emitter(); let n=0; const listener=()=>n++; const a=e.subscribe(listener); const b=e.subscribe(listener); a(); a(); e.emit(0); expect(n===1); b(); e.emit(0); expect(n===1); const f=new module.Emitter();const order:string[]=[];const shared=()=>order.push("same");f.subscribe(shared);f.subscribe(()=>order.push("other"));const last=f.subscribe(shared);last();f.emit(0);expect(order.join()==="same,other"); });
check("order_and_payload", () => { const e=new module.Emitter(); const hits:number[]=[]; e.subscribe((n:number)=>hits.push(n)); e.subscribe((n:number)=>hits.push(n+1)); e.emit(4); expect(hits.join()==="4,5"); });
check("exception_propagation", () => { const e=new module.Emitter(); const error=new Error("sentinel"); e.subscribe(()=>{throw error;}); let caught;try{e.emit(0);}catch(x){caught=x;} expect(caught===error); });
console.log(JSON.stringify({groups}));
process.exitCode=groups.every(g=>g.passed)?0:1;
