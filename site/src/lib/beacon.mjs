// The website's page-view counter: one GET to the relay per page with the path and the referrer's host, and nothing
// else (no cookie, no id, no storage, credentials omitted). Skipped with Do Not Track or Global Privacy Control, and off
// the real site. The relay keeps only per-day counts (tools/umbra-relay/stats.js).
export const RELAY = "https://umbra-buddy-relay.umbra-server.workers.dev";

export const beacon = `(function(){try{
var n=navigator;if(n.doNotTrack==="1"||n.globalPrivacyControl||location.hostname!=="aminechr54.github.io")return;
var r="";try{r=document.referrer?new URL(document.referrer).hostname:"";}catch(e){}
if(r===location.hostname)r="";
var p=location.pathname.replace(/^\\/umbra(?=\\/|$)/,"")||"/";
fetch("${RELAY}/stats/view?p="+encodeURIComponent(p)+"&r="+encodeURIComponent(r),{mode:"no-cors",credentials:"omit",keepalive:true}).catch(function(){});
}catch(e){}})();`;
