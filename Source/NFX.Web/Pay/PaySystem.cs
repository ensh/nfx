/*<FILE_LICENSE>
* NFX (.NET Framework Extension) Unistack Library
* Copyright 2003-2014 IT Adapter Inc / 2015 Aum Code LLC
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
</FILE_LICENSE>*/

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

using NFX.Environment;
using NFX.Financial;
using NFX.ServiceModel;
using NFX.ApplicationModel;

namespace NFX.Web.Pay
{
  /// <summary>
  /// Base class for IPaySystemImplementation implementation.
  /// Instances of descendants of this class is typically created and configured in PaySystemStarter class.
  /// Then particular pay system can be obtained from PaySystem.Instances[PAY_SYSTEM_NAME] indexer
  /// </summary>
  public abstract class PaySystem : Service, IWebClientCaller, IPaySystemImplementation
  {
    #region CONSTS

      public const string CONFIG_PAYMENT_PROCESSING_SECTION = "payment-processing";
      public const string CONFIG_PAY_SYSTEM_HOST_SECTION = "pay-system-host";
      public const string CONFIG_PAY_SYSTEM_SECTION = "pay-system";
      public const string CONFIG_AUTO_START_ATTR = "auto-start";

      private static readonly TimeSpan INSTR_INTERVAL = TimeSpan.FromMilliseconds(4015);

    #endregion

    #region Static

      private static IPaySystemHost s_PaySystemHost;

      /// <summary>
      /// Returns process-global pay system host used to resolve accounts and transactions
      /// or throws if host is not set. Check IsPaySystemHost to see if host is set.
      /// This design provides an indirection level between pay systems (like Stripe, PayPal, Bank etc.) and 
      /// particular application data store implementation as it decouples system-internal formats of transaction and 
      /// account storage from provider-internal data (i.e. PayPal payment token string)
      /// </summary>
      public static IPaySystemHost PaySystemHost 
      { 
        get 
        {
          var result = s_PaySystemHost;

          if (result == null) 
            throw new PaymentException(StringConsts.PAYMENT_SYSTEM_HOST_NULL_ERROR);

          return result;
        }
      }

      public static bool IsPaySystemHost { get { return s_PaySystemHost != null; } }

      /// <summary>
      /// Sets process-global pay system host used to resolve accounts and transactions.
      /// If PayStarter is used then the host may be auto-injected from configuration if it is there.
      /// Developers: Do not call this method as it is used for dependency injection from system code
      /// </summary>
      public static void ___SetPaySystemHost(IPaySystemHostImplementation host)
      {
        s_PaySystemHost = host;
      }


      private static Registry<PaySystem> s_Instances = new Registry<PaySystem>();

      /// <summary>
      /// Returns the read-only registry view of payment systems currently activated
      /// </summary>
      public static IRegistry<IPaySystem> Instances { get { return s_Instances; } }

      /// <summary>
      /// Automatically starts systems designated in config with auto-start attribute
      /// </summary>
      public static void AutoStartSystems()
      {
        App.Instance.RegisterAppFinishNotifiable(PayProcessingFinisher.Instance);

        WebSettings.RequireInitializedSettings();

        var pHost = App.ConfigRoot[WebSettings.CONFIG_WEBSETTINGS_SECTION][CONFIG_PAYMENT_PROCESSING_SECTION][CONFIG_PAY_SYSTEM_HOST_SECTION];
        if (pHost.Exists)
        {
          var host = FactoryUtils.MakeAndConfigure<IPaySystemHostImplementation>(pHost);
          ___SetPaySystemHost(host);          
        }

        foreach (var psNode in App.ConfigRoot[WebSettings.CONFIG_WEBSETTINGS_SECTION][CONFIG_PAYMENT_PROCESSING_SECTION]
                              .Children
                              .Where(cn => cn.IsSameName(CONFIG_PAY_SYSTEM_SECTION)))
        {
          var name = psNode.AttrByName(Configuration.CONFIG_NAME_ATTR).Value;

          if (!psNode.AttrByName(CONFIG_AUTO_START_ATTR).ValueAsBool()) continue;

          var system = FactoryUtils.MakeAndConfigure<PaySystem>(psNode, typeof(PaySystem), new object[] { null, psNode });

          if (s_Instances[system.Name] != null)  // already started
            throw new PaymentException("AutoStart: " + StringConsts.PAYMENT_SYSTEM_DUPLICATE_NAME_ERROR.Args(system.GetType().FullName, system.Name));

          system.Start();
        }
      }

      public static TPaySystem Make<TPaySystem>(string name, IConfigSectionNode node) where TPaySystem: PaySystem
      {
        WebSettings.RequireInitializedSettings();

        return FactoryUtils.MakeAndConfigure<TPaySystem>(node, typeof(TPaySystem), new object[] {name, node});
      }

      public static TPaySystem Make<TPaySystem>(string name, string cfgStr, string format = Configuration.CONFIG_LACONIC_FORMAT) where TPaySystem: PaySystem
      {
        WebSettings.RequireInitializedSettings();

        var cfg = Configuration.ProviderLoadFromString(cfgStr, format);
        return Make<TPaySystem>(name, cfg.Root);
      }

    #endregion

    #region Inner classes

      private class PayProcessingFinisher: IApplicationFinishNotifiable
      {
        internal static readonly PayProcessingFinisher Instance = new PayProcessingFinisher();

        public string Name { get { return GetType().FullName; } }

        public void ApplicationFinishBeforeCleanup(IApplication application)
        {
          foreach (var paySystem in s_Instances)
            paySystem.WaitForCompleteStop();
        }

        public void ApplicationFinishAfterCleanup(IApplication application) {}
      }

    #endregion

    #region ctor

      protected PaySystem(string name, IConfigSectionNode node): this(name, node, null) {}

      protected PaySystem(string name, IConfigSectionNode node, object director): base(director)
      {
        KeepAlive = true;
        Pipelined = true;

        if (node != null)
        {
          Configure(node);
          this.Name = node.AttrByName(Configuration.CONFIG_NAME_ATTR).Value;
        }

        if (name.IsNotNullOrWhiteSpace()) this.Name = name;

        if (this.Name.IsNullOrWhiteSpace()) this.Name = GetType().Name;

        m_Sessions = new List<PaySession>();
      }

    #endregion

    #region Pvt/Prot/Int Fields
      
      private bool m_InstrumentationEnabled;

      private IConfigSectionNode m_DefaultSesssionConnParamsCfg;
      private PayConnectionParameters m_DefaultSessionConnectParams;

      protected internal readonly List<PaySession> m_Sessions;

      private int m_WebServiceCallTimeoutMs;


      private long m_stat_ChargeCount, m_stat_ChargeErrorCount;
      private ConcurrentDictionary<string, decimal> m_stat_ChargeAmounts = new ConcurrentDictionary<string,decimal>();

      private long m_stat_CaptureCount, m_stat_CaptureErrorCount;
      private ConcurrentDictionary<string, decimal> m_stat_CaptureAmounts = new ConcurrentDictionary<string,decimal>();

      private long m_stat_RefundCount, m_stat_RefundErrorCount;
      private ConcurrentDictionary<string, decimal> m_stat_RefundAmounts = new ConcurrentDictionary<string,decimal>();

      private long m_stat_TransferCount, m_stat_TransferErrorCount;
      private ConcurrentDictionary<string, decimal> m_stat_TransferAmounts = new ConcurrentDictionary<string,decimal>();

    #endregion

    #region Public properties

      [Config("default-session-connect-params")]
      public IConfigSectionNode DefaultSesssionConnectParamsCfg
      {
        get { return m_DefaultSesssionConnParamsCfg; }
        set
        {
          m_DefaultSessionConnectParams = MakeDefaultSessionConnectParams(value);
          m_DefaultSesssionConnParamsCfg = value;
        }
      }

   
    #endregion

    #region Public methods

      /// <summary>
      /// Starts new pay session of system-specific type
      /// </summary>
      public PaySession StartSession(PayConnectionParameters cParams = null)
      {
        return DoStartSession(cParams);
      }

      protected abstract PaySession DoStartSession(PayConnectionParameters cParams = null);

      public abstract PaymentException VerifyPotentialTransaction(PaySession session, ITransactionContext context, bool transfer, IActualAccountData from, IActualAccountData to, Amount amount);

      public abstract Transaction Charge(PaySession session, ITransactionContext context, Account from, Account to, Amount amount, bool capture = true, string description = null, object extraData = null);

      public abstract void Capture(PaySession session, ITransactionContext context, ref Transaction charge, Amount? amount = null, string description = null, object extraData = null);

      public abstract Transaction Refund(PaySession session, ITransactionContext context, ref Transaction charge, Amount? amount = null, string description = null, object extraData = null);

      public abstract Transaction Transfer(PaySession session, ITransactionContext context, Account from, Account to, Amount amount, string description = null, object extraData = null);

      #region IInstrumentable implementation

        /// <summary>
        /// Implements IInstrumentable
        /// </summary>
        [Config(Default=false)]
        [ExternalParameter(CoreConsts.EXT_PARAM_GROUP_INSTRUMENTATION, CoreConsts.EXT_PARAM_GROUP_PAY)]
        public bool InstrumentationEnabled
        {
          get { return m_InstrumentationEnabled; }
          set 
          { 
            m_InstrumentationEnabled = value;
            var evt = App.EventTimer.Events[instrTimerEventName()];
            if (evt==null)
            {
              if (!value) return;
              new Time.Event(App.EventTimer, instrTimerEventName(), e => DoAcceptManagerVisit(this, e.LocalizedTime), INSTR_INTERVAL);
            }
            else
            {
              if (value) return;
              evt.Dispose();
            }
          }
        }

        /// <summary>
        /// Returns named parameters that can be used to control this component
        /// </summary>
        public IEnumerable<KeyValuePair<string, Type>> ExternalParameters { get { return ExternalParameterAttribute.GetParameters(this); } }

        /// <summary>
        /// Returns named parameters that can be used to control this component
        /// </summary>
        public IEnumerable<KeyValuePair<string, Type>> ExternalParametersForGroups(params string[] groups)
        {
          return ExternalParameterAttribute.GetParameters(this, groups);
        }

        /// <summary>
        /// Gets external parameter value returning true if parameter was found
        /// </summary>
        public bool ExternalGetParameter(string name, out object value, params string[] groups)
        {
          return ExternalParameterAttribute.GetParameter(this, name, out value, groups);
        }

        /// <summary>
        /// Sets external parameter value returning true if parameter was found and set
        /// </summary>
        public bool ExternalSetParameter(string name, object value, params string[] groups)
        {
          return ExternalParameterAttribute.SetParameter(this, name, value, groups);
        }

      #endregion

    #endregion

    #region IWebClientCaller

      [Config(Default = 20000)]
      public int WebServiceCallTimeoutMs
      {
        get { return m_WebServiceCallTimeoutMs; }
        set { m_WebServiceCallTimeoutMs = value < 0 ? 0 : value; }
      }

      [Config(Default = true)]
      public bool KeepAlive { get; set; }

      [Config(Default = true)]
      public bool Pipelined { get; set; }

    #endregion

    #region Protected

      protected PayConnectionParameters DefaultSessionConnectParams
      {
        get { return m_DefaultSessionConnectParams; }
      }

      protected override void DoConfigure(IConfigSectionNode node)
      {
        if (node == null)
        {
          node = App.ConfigRoot[CONFIG_PAYMENT_PROCESSING_SECTION];
          if (!node.Exists) return;

          //1 try to find the server with the same name as this instance
          var snode = node.Children.FirstOrDefault(cn => cn.IsSameName(CONFIG_PAY_SYSTEM_SECTION) && cn.IsSameNameAttr(Name));

          //2 try to find a server without a name
          if (snode == null)
            snode = node.Children.FirstOrDefault(cn => cn.IsSameNameAttr(CONFIG_PAY_SYSTEM_SECTION) && cn.AttrByName(Configuration.CONFIG_NAME_ATTR).Value.IsNullOrWhiteSpace());

          if (snode == null) return;

          node = snode;
        }

        ConfigAttribute.Apply(this, node);
      }

      protected override void DoStart()
      {
        if (!s_Instances.Register(this))
          throw new PaymentException(StringConsts.PAYMENT_SYSTEM_DUPLICATE_NAME_ERROR.Args(GetType().FullName, Name));
      }

      protected override void DoWaitForCompleteStop()
      {
        s_Instances.Unregister(this);
      }

      protected override void DoAcceptManagerVisit(object manager, DateTime managerNow)
      {
        dumpStats();
      }

      protected abstract PayConnectionParameters MakeDefaultSessionConnectParams(IConfigSectionNode paramsSection);

      #region Stat

        protected void StatChargeError()
        {
          Interlocked.Increment(ref m_stat_ChargeErrorCount);
        }

        protected void StatCharge(Amount amount)
        {
          Interlocked.Increment(ref m_stat_ChargeCount);
          m_stat_ChargeAmounts.AddOrUpdate(amount.CurrencyISO, amount.Value, (k, v) => v + amount.Value);
        }

        protected void StatCaptureError()
        {
          Interlocked.Increment(ref m_stat_CaptureErrorCount);
        }

        protected void StatCapture(Transaction charge, Amount? amount)
        {
          Interlocked.Increment(ref m_stat_CaptureCount);
          var instrAmount = amount ?? charge.Amount;
          m_stat_CaptureAmounts.AddOrUpdate(instrAmount.CurrencyISO, instrAmount.Value, (k, v) => v + instrAmount.Value);
        }

        protected void StatRefundError()
        {
          Interlocked.Increment(ref m_stat_RefundErrorCount);
        }

        protected void StatRefund(Transaction charge, Amount? amount)
        {
          Interlocked.Increment(ref m_stat_RefundCount);
          var instrAmount = amount ?? charge.Amount;
          m_stat_RefundAmounts.AddOrUpdate(instrAmount.CurrencyISO, instrAmount.Value, (k, v) => v + instrAmount.Value);
        }

        protected void StatTransferError()
        {
          Interlocked.Increment(ref m_stat_TransferErrorCount);
        }

        protected void StatTransfer(Amount amount)
        {
          Interlocked.Increment(ref m_stat_TransferCount);
          m_stat_TransferAmounts.AddOrUpdate(amount.CurrencyISO, amount.Value, (k, v) => v + amount.Value);
        }

      #endregion

    #endregion

    #region .pvt .impl

                    private string instrTimerEventName()
                    {
                      return "PaySystem::" + GetType().FullName + "-" + Name;
                    }


                    private void dumpStats()
                    {
                      var src = this.Name;

                      #region Charge

                        Instrumentation.ChargeCount.Record(src, m_stat_ChargeCount);
                        m_stat_ChargeCount = 0;

                        Instrumentation.ChargeErrorCount.Record(src, m_stat_ChargeErrorCount);
                        m_stat_ChargeErrorCount = 0;

                        foreach (var a in m_stat_ChargeAmounts)
                        {
                          string key = a.Key;
                          decimal val = a.Value;

                          while (true)
                          {
                            if (m_stat_ChargeAmounts.TryUpdate(key, 0, val)) break;
                            m_stat_ChargeAmounts.TryGetValue(key, out val); // never fails because keys (currency ISO) are never removed from dictionary
                          }

                          Instrumentation.ChargeAmount.Record(src, new Amount(key, val));
                        } 

                      #endregion

                      #region Capture

                        Instrumentation.CaptureCount.Record(src, m_stat_CaptureCount);
                        m_stat_CaptureCount = 0;

                        Instrumentation.CaptureErrorCount.Record(src, m_stat_CaptureErrorCount);
                        m_stat_CaptureErrorCount = 0;

                        foreach (var a in m_stat_CaptureAmounts)
                        {
                          string key = a.Key;
                          decimal val = a.Value;

                          while (true)
                          {
                            if (m_stat_CaptureAmounts.TryUpdate(key, 0, val)) break;
                            m_stat_CaptureAmounts.TryGetValue(key, out val); // never fails because keys (currency ISO) are never removed from dictionary
                          }

                          Instrumentation.CaptureAmount.Record(src, new Amount(key, val));
                        } 

                      #endregion

                      #region Refund

                        Instrumentation.RefundCount.Record(src, m_stat_RefundCount);
                        m_stat_RefundCount = 0;

                        Instrumentation.RefundErrorCount.Record(src, m_stat_RefundErrorCount);
                        m_stat_RefundErrorCount = 0;

                        foreach (var a in m_stat_RefundAmounts)
                        {
                          string key = a.Key;
                          decimal val = a.Value;

                          while (true)
                          {
                            if (m_stat_RefundAmounts.TryUpdate(key, 0, val)) break;
                            m_stat_RefundAmounts.TryGetValue(key, out val); // never fails because keys (currency ISO) are never removed from dictionary
                          }

                          Instrumentation.RefundAmount.Record(src, new Amount(key, val));
                        } 

                      #endregion

                      #region Transfer

                        Instrumentation.TransferCount.Record(src, m_stat_TransferCount);
                        m_stat_TransferCount = 0;

                        Instrumentation.TransferErrorCount.Record(src, m_stat_TransferErrorCount);
                        m_stat_TransferErrorCount = 0;

                        foreach (var a in m_stat_TransferAmounts)
                        {
                          string key = a.Key;
                          decimal val = a.Value;

                          while (true)
                          {
                            if (m_stat_TransferAmounts.TryUpdate(key, 0, val)) break;
                            m_stat_TransferAmounts.TryGetValue(key, out val); // never fails because keys (currency ISO) are never removed from dictionary
                          }

                          Instrumentation.TransferAmount.Record(src, new Amount(key, val));
                        } 

                      #endregion
                    }

      #endregion

  }
}
