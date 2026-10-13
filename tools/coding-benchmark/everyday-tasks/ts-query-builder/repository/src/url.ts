export function joinPath(base:string,path:string):string {return base.replace(/\/$/,"")+"/"+path.replace(/^\//,"");}
export function buildQuery(params:Record<string,string|number|boolean|null|undefined>):string {throw new Error("not implemented");}
