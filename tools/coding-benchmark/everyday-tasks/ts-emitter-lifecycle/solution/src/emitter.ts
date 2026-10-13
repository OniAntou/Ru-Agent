export class Emitter<T> {
  private listeners: ((value:T)=>void)[]=[];
  subscribe(listener:(value:T)=>void):()=>void {
    const registration=(value:T)=>listener(value);
    this.listeners.push(registration);
    let active=true;
    return ()=> { if(!active) return; active=false; const index=this.listeners.indexOf(registration); if(index>=0) this.listeners.splice(index,1); };
  }
  emit(value:T):void { for(const listener of [...this.listeners]) listener(value); }
}
