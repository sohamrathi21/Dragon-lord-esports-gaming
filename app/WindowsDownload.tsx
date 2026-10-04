import { Download, Monitor, ArrowLeft } from 'lucide-react';
const env=(import.meta as unknown as {env:Record<string,string>}).env;
const url=env.VITE_WINDOWS_INSTALLER_URL||'';
const version=env.VITE_WINDOWS_APP_VERSION||'';
const size=env.VITE_WINDOWS_DOWNLOAD_SIZE||'';
const windows=env.VITE_WINDOWS_SUPPORTED_VERSIONS||'';
let secure=false;try{const u=new URL(url);secure=u.protocol==='https:'&&/\.(exe|msi)$/i.test(u.pathname)&&!u.username&&!u.password;}catch{}
const available=secure&&!!version&&!!size&&!!windows;
export default function WindowsDownload({standalone=false}:{standalone?:boolean}){
 return <section className={`panel download-section ${standalone?'standalone-download':''}`} aria-labelledby='windows-download-title'>
 {standalone&&<a className='text-button' href='/'><ArrowLeft size={16}/>Back to Dragon Lord</a>}
 <span className='eyebrow'>DRAGON LORD ESPORTS GAMING</span><Monitor size={36}/><h2 id='windows-download-title'>Download for Windows</h2><p className='muted'>Install the Dragon Lord desktop application on this PC.</p>
 <dl className='download-details'><div><dt>App version</dt><dd>{version||'To be announced'}</dd></div><div><dt>Download size</dt><dd>{size||'To be announced'}</dd></div><div><dt>Supported Windows</dt><dd>{windows||'To be announced'}</dd></div></dl>
 {available?<a className='button primary' href={url} download rel='noopener noreferrer'><Download size={18}/>Download for Windows</a>:<button className='button secondary' disabled aria-describedby='release-status'>Coming soon</button>}
 <p>Download the installer, open it, and follow the setup instructions.</p>
 {!available&&<p id='release-status' className='fine-print'>The Windows installer is not available yet. Release details will appear here when it is published.</p>}
 </section>;
}
