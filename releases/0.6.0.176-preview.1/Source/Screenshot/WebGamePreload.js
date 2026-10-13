(()=>{'use strict';const t=createFusionWebTransport();if(t)require('electron').contextBridge.exposeInMainWorld('__fusionWebTransport',t);})();
