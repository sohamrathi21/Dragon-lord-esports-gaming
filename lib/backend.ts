import { HubConnectionBuilder } from '@microsoft/signalr';
import type { State } from './cafe';
const env = (import.meta as unknown as { env?: Record<string,string> }).env;
const base = env?.VITE_API_URL || '';
export const demoRequested = () => typeof window !== 'undefined' && new URLSearchParams(window.location.search).get('demo') === '1';
export async function request<T = any>(path:string, body?:unknown, key?:string):Promise<T>{
 const response=await fetch(base+path,{method:body===undefined?'GET':'POST',credentials:'include',headers:{'Content-Type':'application/json','X-Dragon-Request':'1',...(key?{'Idempotency-Key':key}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});
 const text=await response.text();let result:any;try{result=text?JSON.parse(text):{};}catch{throw Error('Backend unavailable. Start the ASP.NET service and reload.');}
 if(!response.ok)throw Error(result.error||result.detail||(response.status===401?'Please sign in.':response.status===403?'Your role cannot perform this action.':`Request failed (${response.status}).`));
 return result;
}
export const loadWorkspace=()=>request<State>('/api/workspace');
export function watchWorkspace(onChange:()=>void,onConnection:(connected:boolean)=>void){
 const connection=new HubConnectionBuilder().withUrl(base+'/hubs/venue',{withCredentials:true}).withAutomaticReconnect().build();
 connection.on('Changed',onChange);connection.onreconnecting(()=>onConnection(false));connection.onreconnected(()=>{onConnection(true);onChange();});connection.onclose(()=>onConnection(false));
 void connection.start().then(()=>onConnection(true)).catch(()=>onConnection(false));
 return ()=>{void connection.stop();};
}
export async function remoteAction(action:string,d:any){
 const key=d.key||crypto.randomUUID();
 if(action==='customer')return request('/api/customers',{name:d.name,email:d.email,member:d.member},key);
 if(action==='payment')return request('/api/payments/cash',{customerId:d.customerId,amountMinor:d.amount,idempotencyKey:key});
 if(action==='start')return request('/api/sessions',{customerId:d.customerId,stationId:d.stationId,minutes:d.minutes,idempotencyKey:key});
 if(action==='end')return request(`/api/sessions/${encodeURIComponent(d.sessionId)}/end`,{reason:'Staff ended session'});
 if(action==='booking')return request('/api/reservations',{customerId:d.customerId,stationIds:d.stations,start:new Date(d.start).toISOString(),end:new Date(d.end).toISOString(),idempotencyKey:key});
 if(action==='order')return request('/api/orders',{customerId:d.customerId,stationId:d.stationId,productId:d.productId,quantity:d.qty,idempotencyKey:key});
 return request('/api/actions/'+encodeURIComponent(action),d,key);
}
export async function checkout(customerId:string,amountMinor:number){
 const intentId=crypto.randomUUID();const order=await request('/api/payments/razorpay/orders',{customerId,amountMinor,intentId});
 if(!(window as any).Razorpay)await new Promise<void>((resolve,reject)=>{const script=document.createElement('script');script.src='https://checkout.razorpay.com/v1/checkout.js';script.onload=()=>resolve();script.onerror=()=>reject(Error('Could not load Razorpay Checkout.'));document.head.appendChild(script);});
 return new Promise<void>((resolve,reject)=>{const Razorpay=(window as any).Razorpay;const pay=new Razorpay({key:order.keyId,order_id:order.orderId,amount:order.amount,currency:order.currency,name:'Dragon Lord Esports Gaming',description:'Wallet top-up',handler:async(r:any)=>{try{const verified=await request('/api/payments/razorpay/verify',{intentId,paymentId:r.razorpay_payment_id,signature:r.razorpay_signature});if(verified.status!=='Captured')throw Error('Payment is awaiting capture. Your wallet will update after verification.');resolve();}catch(e){reject(e);}},modal:{ondismiss:()=>reject(Error('Checkout closed. No wallet credit applied.'))},theme:{color:'#ed4149'}});pay.open();});
}
