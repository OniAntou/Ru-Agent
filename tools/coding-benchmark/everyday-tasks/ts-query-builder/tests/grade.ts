import {join} from "node:path";
import {pathToFileURL} from "node:url";
const module = await import(pathToFileURL(join(process.env.BENCHMARK_CANDIDATE_ROOT!,"src","url.ts" )).href);
const groups: {name:string;passed:boolean;checks:number;failures:string[]}[]=[];
function expect(value:boolean) {if(!value) throw new Error("behavior mismatch");}
function check(name:string,test:()=>void) {try {test();groups.push({name,passed:true,checks:1,failures:[]});} catch(error) {groups.push({name,passed:false,checks:1,failures:[String(error)]});}}
check("sorted_keys", () => { expect(module.buildQuery({z:3,a:1,m:2})==="a=1&m=2&z=3"); });
check("falsy_and_missing", () => { expect(module.buildQuery({zero:0,no:false,empty:"",absent:null,unknown:undefined})==="empty=&no=false&zero=0"); });
check("encoding", () => { expect(module.buildQuery({"a b":"x+y &"})==="a+b=x%2By+%26"); const params=new URLSearchParams(module.buildQuery({"tên":"Đặng"})); expect(params.get("tên")==="Đặng"); });
check("empty_and_own_keys", () => { expect(module.buildQuery({})==="");const p=Object.assign(Object.create({hidden:"bad"}),{own:"ok"});expect(module.buildQuery(p)==="own=ok"); });
check("preserve_input", () => { const p=Object.freeze({b:2,a:"x"});expect(module.buildQuery(p)==="a=x&b=2");expect(Object.keys(p).join()==="b,a"); });
check("existing_join", () => { expect(module.joinPath("https://example.test/","/items")==="https://example.test/items"); });
console.log(JSON.stringify({groups}));
process.exitCode=groups.every(g=>g.passed)?0:1;
