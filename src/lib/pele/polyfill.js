(function(){
function def(o,n,f){if(!o[n])Object.defineProperty(o,n,{value:f,writable:true,configurable:true});}
var S=String.prototype,A=Array.prototype;
def(S,'trimStart',function(){return this.replace(/^[\s﻿\xA0]+/,'');});
def(S,'trimEnd',function(){return this.replace(/[\s﻿\xA0]+$/,'');});
def(S,'trimLeft',S.trimStart);def(S,'trimRight',S.trimEnd);
def(S,'replaceAll',function(a,b){if(a instanceof RegExp)return this.replace(a,b);return this.split(a).join(typeof b==='function'?b(a):b);});
def(S,'at',function(i){i=Math.trunc(i)||0;if(i<0)i+=this.length;return i<0||i>=this.length?undefined:this.charAt(i);});
def(S,'matchAll',function(re){var fl=typeof re==='string'?'':(re.flags!==undefined?re.flags:(re.global?'g':'')+(re.ignoreCase?'i':'')+(re.multiline?'m':'')+(re.unicode?'u':'')+(re.sticky?'y':''));var r=new RegExp(typeof re==='string'?re:re.source,fl.indexOf('g')<0?fl+'g':fl),s=String(this),out=[],m;while((m=r.exec(s))!==null){out.push(m);if(m[0]==='')r.lastIndex++;}return out[Symbol.iterator]();});
def(A,'at',function(i){i=Math.trunc(i)||0;if(i<0)i+=this.length;return this[i];});
def(A,'flat',function(d){d=d===undefined?1:d;var r=[];(function f(a,k){for(var i=0;i<a.length;i++){if(Array.isArray(a[i])&&k>0)f(a[i],k-1);else r.push(a[i]);}})(this,d);return r;});
def(A,'flatMap',function(fn,t){return A.map.call(this,fn,t).flat(1);});
def(A,'findLast',function(fn,t){for(var i=this.length-1;i>=0;i--)if(fn.call(t,this[i],i,this))return this[i];});
def(A,'findLastIndex',function(fn,t){for(var i=this.length-1;i>=0;i--)if(fn.call(t,this[i],i,this))return i;return -1;});
def(A,'toSorted',function(c){return this.slice().sort(c);});
def(A,'toReversed',function(){return this.slice().reverse();});
def(Object,'fromEntries',function(it){var o={};for(var e of it)o[e[0]]=e[1];return o;});
def(Object,'hasOwn',function(o,k){return Object.prototype.hasOwnProperty.call(o,k);});
if(typeof globalThis==='undefined')Function('return this')().globalThis=Function('return this')();
})();
