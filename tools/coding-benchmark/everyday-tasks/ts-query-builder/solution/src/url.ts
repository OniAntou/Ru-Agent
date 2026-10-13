export function joinPath(base:string,path:string):string {return base.replace(/\/$/,"")+"/"+path.replace(/^\//,"");}
export function buildQuery(params:Record<string,string|number|boolean|null|undefined>):string {const query=new URLSearchParams();
  for(const key of Object.keys(params).sort()) {const value=params[key];if(value!==null&&value!==undefined) query.append(key,String(value));}
  return query.toString();}
