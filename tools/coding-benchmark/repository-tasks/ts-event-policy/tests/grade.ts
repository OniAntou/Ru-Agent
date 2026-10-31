import {spawnSync} from "node:child_process";
import {join} from "node:path";
import {pathToFileURL} from "node:url";
const root = process.env.BENCHMARK_CANDIDATE_ROOT!;
const module = await import(pathToFileURL(join(root, "src", "index.ts")).href);
const groups: {name:string;passed:boolean;checks:number;failures:string[]}[] = [];
function check(name:string, test:()=>boolean) { try { if(!test()) throw new Error("behavior mismatch"); groups.push({name,passed:true,checks:1,failures:[]}); } catch(error) { groups.push({name,passed:false,checks:1,failures:[String(error)]}); } }
const typecheck = spawnSync("tsc", ["--noEmit", "--pretty", "false"], {cwd:root, encoding:"utf8", timeout:55000});
groups.push({name:"cross_file_typecheck",passed:typecheck.status===0,checks:1,failures:typecheck.status===0?[]:[(typecheck.stderr||typecheck.stdout||"tsc failed").slice(0,500)]});
check("normalized_event_filter", () => { const events=[{name:" CREATE ",payload:"a"},{name:"delete",payload:"b"}]; const result=module.filterEvents({allowedNames:["create"]},events); return result.length===1 && result[0]===events[0]; });
check("safe_default", () => !module.isAllowed({allowedNames:["create"]},"unknown") && !module.isAllowed({allowedNames:[]},"create"));
check("result_snapshot", () => { const input=[{name:"create",payload:"a"}]; const result=module.filterEvents({allowedNames:["create"]},input); return result!==input && result.length===1 && result[0]===input[0]; });
check("model_exports", () => typeof module.normalizeName==="function" && typeof module.filterEvents==="function");
check("empty_input", () => { const input: unknown[]=[]; const result=module.filterEvents({allowedNames:["create"]},input); return result.length===0 && result!==input; });
console.log(JSON.stringify({groups}));
process.exitCode=groups.every(group=>group.passed)?0:1;
