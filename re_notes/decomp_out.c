// ===== EB.Net.TcpClientFactory$$Create @ 0x1236ce8 =====

undefined8 EB_Net_TcpClientFactory__Create(uint param_1,uint param_2)

{
  undefined8 uVar1;
  
  if ((DAT_02f3f9b4 & 1) == 0) {
    thunk_FUN_00a5ef54(PTR_DAT_02cc0ba0);
    DAT_02f3f9b4 = 1;
  }
  uVar1 = thunk_FUN_00a60028(*(undefined8 *)PTR_DAT_02cc0ba0);
  EB_Net_TcpClientBouncy___ctor(uVar1,param_1 & 1,param_2 & 1);
  return uVar1;
}



