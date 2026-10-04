import React from 'react';
import { createRoot } from 'react-dom/client';
import Cafe from './Cafe';
import WindowsDownload from './WindowsDownload';
import './globals.css';
createRoot(document.getElementById('root')!).render(<React.StrictMode>{window.location.pathname === '/download' ? <div className='account-screen'><WindowsDownload standalone/></div> : <Cafe/>}</React.StrictMode>);
