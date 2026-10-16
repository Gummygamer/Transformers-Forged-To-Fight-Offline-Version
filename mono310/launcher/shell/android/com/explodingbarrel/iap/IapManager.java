package com.explodingbarrel.iap;

/** Newly authored stand-in: purchases are not available offline. */
final class Manager {
    private Manager() { }
    public static void Init(String publicKey, boolean debug) { }
    public static void PurchaseItem(String productId, String payload, String developerPayload) { }
    public static void CheckPromoEligible() { }
    public static void Enumerate(String productIds) { }
    public static void CompletePurchase(String payload, String signature) { }
}
