import {buildQuery,joinPath} from "./src/url.ts";
if(buildQuery({b:2,a:"x y",off:false})!=="a=x+y&b=2&off=false") throw new Error("query builder failed");
if(joinPath("https://example.test/","/items")!=="https://example.test/items") throw new Error("joinPath changed");
console.log("smoke passed");
