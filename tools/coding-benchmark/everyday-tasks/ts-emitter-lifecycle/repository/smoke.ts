import {Emitter} from "./src/emitter.ts";
const e=new Emitter<number>(); let n=0; const off=e.subscribe(()=>{}); e.subscribe(()=>n++);
off();off();e.emit(1);if(n!==1) throw new Error("double unsubscribe removed another listener");
console.log("smoke passed");
