export class Emitter<T> {
  private listeners: ((value:T)=>void)[]=[];
  subscribe(listener:(value:T)=>void):()=>void {
    this.listeners.push(listener);
    return ()=> { this.listeners.splice(this.listeners.indexOf(listener),1); };
  }
  emit(value:T):void { for(const listener of this.listeners) listener(value); }
}
