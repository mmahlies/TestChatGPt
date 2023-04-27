using Billing.BL.BillingEngine.ManagersFactory.Interfaces;
using Billing.BL.Managers.Interfaces;
using Billing.BL.ManagersFactory.Interfaces;
using Billing.Enums;
using Billing.Support;
using Billing.Utilities.Support;
using HMIS.Billing.Model;
using HMIS.Billing.Model.Custom.BillingEngine;
using HMIS.Core.Exception.BillingException;
using HMIS.Core.Log;
using HMIS.Core.Log.Enums;
using HMIS.Core.Repository.Entity.Custom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Transactions;
using System.Web.Script.Serialization;

namespace Billing.BL.Managers
{
	public class BillingEngineManagerFactory : IBillingEngineManagerFactory
	{



        protected ICashEngine _CashEngine;
        protected IInsuredEngine _InsuredEngine;
        protected IBilling_UnitOfWork _unitOfWork;
        protected IPatientTransactionManager _PatientTransactionMngr;
        protected ITaxCalculatorManagerFactory _TaxCalculatorManagerFctry;
        protected ITaxManager _TaxManager;
        protected IPatientTransactionTaxMasterManager _PatientTransactionTaxMasterMngr;
        protected IPackageManager _packageManager;
        protected long transactionTimeOut;
        protected IPatientTransactionInMemoryManager _PatientTransactionInMemoryManger;
        protected IConfigurationManager _ConfigurationMngr;
        protected IPatientTransactionTaxManager _patientTransactionTaxManager;
        protected IUserInfo _UserInfo;
        public BillingEngineManagerFactory(IUserInfo userInfo, ICashEngine cashEngine, IInsuredEngine insuredEngine, IPatientTransactionManager patientTransactionManager,
            IBilling_UnitOfWork unitOfWork, ITaxCalculatorManagerFactory taxCalculatorManagerFctry, ITaxManager taxManager,
            IPatientTransactionTaxMasterManager patientTransactionTaxMasterMngr, IPackageManager packageManager,
            IPatientTransactionInMemoryManager patientTransactionInMemoryManger, IConfigurationManager ConfigurationMngr,
            IPatientTransactionTaxManager patientTransactionTaxManager)
        {
            #region Set Engine Managers

			this._CashEngine = cashEngine;
			this._InsuredEngine = insuredEngine;
			_PatientTransactionMngr = patientTransactionManager;
			_unitOfWork = unitOfWork;
			_TaxCalculatorManagerFctry = taxCalculatorManagerFctry;
			_TaxManager = taxManager;
			_PatientTransactionTaxMasterMngr = patientTransactionTaxMasterMngr;
			_packageManager = packageManager;
			_PatientTransactionInMemoryManger = patientTransactionInMemoryManger;
			_ConfigurationMngr = ConfigurationMngr;
			_patientTransactionTaxManager = patientTransactionTaxManager;
			_UserInfo = userInfo;
			transactionTimeOut = long.Parse(HMIS.Core.Utilities.Support.Configuration.GetConfig("transactionTimeOut"));


			#endregion
		}

        /// <summary>
        /// Get price of specific product
        /// </summary>
        /// <param name="parameters">List of parameters from visit management request</param>
        /// <returns>List of model of price inquery</returns>
        public virtual List<PriceInquiryModel> PriceInquiry(List<PriceInquiryParameters> parameters, bool savePatientTransaction = false, bool reInquiry = false)
        {
            List<PriceInquiryModel> result = new List<PriceInquiryModel>();
            _UserInfo.OrgId = parameters.FirstOrDefault().EntityId != null ? parameters.FirstOrDefault().EntityId.Value : _UserInfo.OrgId;
            if (parameters == null || parameters.Count == 0)
            {
                result.Add(new PriceInquiryModel()
                {
                    StatusCode = BillingEnums.BillingEngineStatusCode.InvalidRequest,
                    IsValid = false,
                    MessageResult = EngineMessages.InvalidRequest
                });
                return result;
            }
            TransactionOptions options = new TransactionOptions();
            options.IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted;
            options.Timeout = TimeSpan.FromMinutes(transactionTimeOut);

			#region handle price inquiry service set VisitServiceID  with dummy values 
			//KSA Limit Effect In Price Inquiry
			// Handle sevice id with dummy id in case price inquery only 
			if (savePatientTransaction == false)
			{
				long count = -1;
				foreach (PriceInquiryParameters item in parameters)
				{
					// Set unique id to each request used in log (Database log in EngineLog table)
					LogAdditionalData logData = new LogAdditionalData { RequestID = Guid.NewGuid(), VisitID = item.VisitID, EntityID = _UserInfo.OrgId };
					// Log in database the inquiry requests
					LogFactory<BillingEngineManagerFactory>.Log(LogLevel.Info, new JavaScriptSerializer().Serialize(item), LogType.Database, null, logData);

					if (item.VisitServiceID == null || item.VisitServiceID == 0)
					{
						item.VisitServiceID = count;
						count = count - 1;
					}
				}
			}

			#endregion

			using (TransactionScope ts = new TransactionScope(TransactionScopeOption.Required, options, TransactionScopeAsyncFlowOption.Enabled))
			{
				try
				{
					result = PriceInquiryProcedures(parameters, savePatientTransaction, reInquiry);
					ts.Complete();
				}
				catch (BillingEngineException ex)
				{
					ts.Dispose();
					throw ex;
				}
				catch (Exception ex)
				{

					ts.Dispose();
					throw new BillingEngineException(ex.GetExceptionMessages(), (int)BillingEnums.BillingExceptionMessagesEnum.FrameworkExceptions);
				}
			}
			return result;
		}

        public virtual List<PriceInquiryModel> PriceInquiryProcedures(List<PriceInquiryParameters> parameters, bool savePatientTransaction, bool reInquiry)
        {
            #region 1 :Prepration Data and validations
            //KSA Limit Effect In Price Inquiry
            //Incase Save Patient Transaction = False
            List<long> DummyVisitServiceIDs = new List<long>();
            ContractTypeInfo contractTypeInfo = new ContractTypeInfo();
            if ((reInquiry || savePatientTransaction) && parameters.Any(a => a.VisitServiceID == null))
                throw new BillingEngineException("VisitServiceID is NULL", (int)BillingEnums.BillingExceptionMessagesEnum.EmptyVisitServiceID);

			List<PriceInquiryModel> result = new List<PriceInquiryModel>();
			long? visitID = parameters.FirstOrDefault().VisitID;
			bool isInsured = parameters[0].FinancialStatus == (int)BillingEnums.PriceListCategory.Insured;
			List<PatientTransactionWithExtraData> patientTransactionsWithExtra = new List<PatientTransactionWithExtraData>();
			List<PatientTransaction> modifyPatientTrans = new List<PatientTransaction>();
			bool hasExceedLimit = false;
			List<bool> exceedLimitList = new List<bool>();

			List<PatientTransactionWithExtraData> editOldPatientTransExceedLimit = new List<PatientTransactionWithExtraData>();
			List<long> excludeClaimedVisitService;
			var latestConfiguration = _ConfigurationMngr.GetLastActiveConfiguration();
			if (visitID.HasValue)
			{
				patientTransactionsWithExtra = _PatientTransactionMngr.GetPatientTransactionsByVisitIDWithExtraDetails(visitID.Value).ToList();
			}

			if (reInquiry)
			{
				// get exclude Claimed Visit Service id and remove it from parameters 
				excludeClaimedVisitService = patientTransactionsWithExtra.Where(pt => pt.PatientTransaction.PatientTransactionDetail.IsClaim == true).Select(vs => vs.PatientTransaction.VisitServiceID.Value).ToList();
				parameters = parameters.Where(p => !excludeClaimedVisitService.Contains(p.VisitServiceID.Value)).ToList();

				//Reset Patient Transaction Price Share
				//_PatientTransactionMngr.ResetPatientTransactionPriceShare(patientTransactionsWithExtra.Where(pt => pt.PatientTransaction.PatientTransactionDetail.IsClaim != true).ToList());
				// in case of insured 
				if (isInsured)
				{
					//Reset Patient Transaction Price Share
					_PatientTransactionMngr.ResetPatientTransactionPriceShare(patientTransactionsWithExtra.Where(pt => pt.PatientTransaction.PatientTransactionDetail.IsClaim != true).ToList());

				}
				// in case of cash 
				else
				{
					//Reset Patient Transaction Price Share
					_PatientTransactionMngr.ResetPatientTransactionPriceShare(patientTransactionsWithExtra.Where(pt => pt.PatientTransaction.PatientPackageID != null).ToList());
				}

			}
			if (isInsured)
			{
				List<long> allContracts = parameters.Where(it => it.ContractID.HasValue).Select(it => it.ContractID.Value).ToList();
				allContracts.AddRange(patientTransactionsWithExtra.Where(it => it.PatientTransaction.ContractID.HasValue).Select(it => it.PatientTransaction.ContractID.Value).ToList());

				List<long> allInsuranceCards = parameters.Where(it => it.InsuranceCardID.HasValue).Select(it => it.InsuranceCardID.Value).ToList();
				allInsuranceCards.AddRange(patientTransactionsWithExtra.Where(it => it.PatientTransaction.InsuranceCardID.HasValue).Select(it => it.PatientTransaction.InsuranceCardID.Value).ToList());

				List<long> allCoverLetters = parameters.Where(it => it.CoverLetterID.HasValue).Select(it => it.CoverLetterID.Value).ToList();
				allCoverLetters.AddRange(patientTransactionsWithExtra.Where(it => it.PatientTransaction.CoverLetterID.HasValue).Select(it => it.PatientTransaction.CoverLetterID.Value).ToList());

				List<long> allContractors = parameters.Where(it => it.ContractorID.HasValue).Select(it => it.ContractorID.Value).Distinct().ToList();
				allContractors.AddRange(patientTransactionsWithExtra.Where(it => it.PatientTransaction.ContractorID.HasValue).Select(it => it.PatientTransaction.ContractorID.Value).ToList());
				_InsuredEngine.IntializeEngine(new EngineProcedureCacheData
				{
					PatientTransactionWithExtraData = patientTransactionsWithExtra,
					ToBeLoadedInsuranceCards = allInsuranceCards.Distinct().ToList(),
					ToBeLoadedContracts = allContracts.Distinct().ToList(),
					ToBeLoadedCoverLetters = allCoverLetters.Distinct().ToList(),
					ToBeLoadedContractors = allContractors.Distinct().ToList(),
					EpisodeType = parameters.Count == 0 ? 0 : parameters[0].EpisodeType ?? 0,
				});
			}
			else
			{
				_CashEngine.IntializeEngine(new EngineProcedureCacheData
				{
					PatientTransactionWithExtraData = patientTransactionsWithExtra,
					EpisodeType = parameters[0].EpisodeType ?? 0
				});
			}

			//  parameters = parameters.OrderBy(a => a.VisitServiceID).ToList();
			List<PriceInquiryParameters> unSavedParameters = parameters.Where(v => v.VisitServiceID < 0).OrderByDescending(r => r.VisitServiceID).ToList();
			List<PriceInquiryParameters> savedParameters = parameters.Where(v => v.VisitServiceID > 0).ToList();
			parameters = savedParameters;
			parameters.AddRange(unSavedParameters);
			foreach (PriceInquiryParameters parameter in parameters)
			{
				if (parameter.FinancialStatus != (int)BillingEnums.PriceListCategory.Cash && parameter.FinancialStatus != (int)BillingEnums.PriceListCategory.Insured)
				{
					result.Add(new PriceInquiryModel()
					{
						StatusCode = BillingEnums.BillingEngineStatusCode.InvalidFinancialStatus,
						IsValid = false,
						MessageResult = EngineMessages.InvalidFinancialStatus,
						TransactionID = parameter.TransactionID,
						VisitServiceID = parameter.VisitServiceID
					});
					continue;
				}



				PatientTransaction patientTransaction = null;
				if (reInquiry && parameter.TransactionID.HasValue)
				{
					patientTransaction = _PatientTransactionInMemoryManger.GetPatientTransactionByID(parameter.TransactionID.Value, patientTransactionsWithExtra);
					if (patientTransaction == null || patientTransaction.IsDeleted != false) //continue;
						throw new BillingEngineException("Transaction of ID " + parameter.TransactionID.Value + " does not exist", (int)BillingEnums.BillingExceptionMessagesEnum.TransactionIDNotExist);
				}
				PatientTransaction visitServicePatientTransaction = null;
				if (patientTransaction != null && patientTransaction.VisitServiceID.Value == parameter.VisitServiceID.Value)
					visitServicePatientTransaction = patientTransaction;
				else if (parameter.VisitServiceID.HasValue)
					visitServicePatientTransaction = _PatientTransactionInMemoryManger.GetPatientTransactionByVisitServiceID(parameter.VisitServiceID.Value, patientTransactionsWithExtra);

				bool isEditMode = visitServicePatientTransaction != null;
				PriceInquiryTransactionalModel model = null;
				DateTime? packageExpiryDate = null;
				DateTime? packageStartDate = null;

				if (savePatientTransaction && isEditMode && visitServicePatientTransaction.PatientPackageID.HasValue && visitServicePatientTransaction.PatientPackageID != parameter.PatientPackageID)
				{
					PatientTransaction packagePurchasingRecord = _PatientTransactionInMemoryManger.PackagesharedMethod(visitServicePatientTransaction.PatientPackageID.Value, patientTransactionsWithExtra);


					// PatientTransaction packagePurchasingRecord = _PatientTransactionInMemoryManger.GetPatientTransactionByID(visitServicePatientTransaction.PatientPackageID.Value, patientTransactionsWithExtra, true);
					if (packagePurchasingRecord == null || (packagePurchasingRecord != null && packagePurchasingRecord.PatientPackage == null))
						throw new BillingEngineException("Transaction of ID " + packagePurchasingRecord.ID + " does not exist", (int)BillingEnums.BillingExceptionMessagesEnum.TransactionIDNotExist);

					//if (packagePurchasingRecord.PatientPackage.IsTimeLimit == true && packagePurchasingRecord.PatientPackage.IsStartFromPurchasingDate == false)
					//{
					//    PatientTransaction firstPerformingPatientTransaction = _PatientTransactionInMemoryManger.GetPackageFirstPerformingTransaction(visitServicePatientTransaction.PatientPackageID.Value, patientTransactionsWithExtra);


					//    if (firstPerformingPatientTransaction != null)
					//    {
					//        packagePurchasingRecord.PatientPackage.StartDate = firstPerformingPatientTransaction.PerformingDate.Value.Date;
					//        packagePurchasingRecord.PatientPackage.ExpiryDate = _PatientTransactionMngr.CaluclateExpiryDate(packagePurchasingRecord.PatientPackage.PackageExpireAfter.Value, packagePurchasingRecord.PatientPackage.TimeUnitID.Value, firstPerformingPatientTransaction.PerformingDate.Value.Date);
					//    }
					//    else
					//    {
					//        packagePurchasingRecord.PatientPackage.StartDate = null;
					//        packagePurchasingRecord.PatientPackage.ExpiryDate = null;
					//    }
					//}
				}

				#endregion
				// Check financial status in current request
				#region 2 : if financial status is cash Call CashPriceInquiry  

				if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Cash)
				{
					model = _CashEngine.CashPriceInquiry(parameter, savePatientTransaction);
					if (savePatientTransaction && model.PriceInquiry.IsValid)
					{
						if (!isEditMode)
							visitServicePatientTransaction = _CashEngine.AddPatientTransaction(model.PatientTransactionModel);
						else
						{
							model.PatientTransactionModel.ID = visitServicePatientTransaction.ID;
							visitServicePatientTransaction = _CashEngine.UpdatePatientTransaction(model.PatientTransactionModel, visitServicePatientTransaction);
						}

						packageExpiryDate = this._CashEngine.GetPackageExpiryDate();
						packageStartDate = this._CashEngine.GetPackageStartDate();
					}
				}
				#endregion

				#region 3 : if financial status if Insured Call InsuredPriceInquiry
				else if (parameter.FinancialStatus == (int)BillingEnums.FinancialStatus.Insured)
				{
					// Priceinquiry for the sent transaction only
					if (reInquiry)
					{
						model = _InsuredEngine.InsuredPriceInquiry(parameter, savePatientTransaction, false, true);
					}
					else
					{
						//KSA Limit Effect In Price Inquiry
						// model = _InsuredEngine.InsuredPriceInquiry(parameter, savePatientTransaction);
						model = _InsuredEngine.InsuredPriceInquiry(parameter, true, true, savePatientTransaction);
					}

					contractTypeInfo.ContractTypeID = model.PriceInquiry.ContractTypeID;
					contractTypeInfo.ContractTypeName = model.PriceInquiry.ContractTypeName;
					contractTypeInfo.ContractTypeArName = model.PriceInquiry.ContractTypeArName;

					//KSA Limit Effect In Price Inquiry
					//if (savePatientTransaction && model.PriceInquiry.IsValid)
					if (model.PriceInquiry.IsValid)
					{
						if (!isEditMode)
						{
							if (latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && model.PatientTransactionModel.PatientTransactionDetailModel.IsExceedLimit == true)
							{
								exceedLimitList.Add(true);
								hasExceedLimit = true;
							}
							visitServicePatientTransaction = _InsuredEngine.AddPatientTransaction(model.PatientTransactionModel, false);
							modifyPatientTrans.Add(visitServicePatientTransaction);
							//  visitServicePatientTransaction = _InsuredEngine.AddPatientTransaction(model.PatientTransactionModel);
						}
						else
						{
							if (latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && model.PatientTransactionModel.PatientTransactionDetailModel.IsExceedLimit == true)
							{
								exceedLimitList.Add(true);
								hasExceedLimit = true;
							}
							visitServicePatientTransaction = _InsuredEngine.UpdatePatientTransaction(model.PatientTransactionModel, visitServicePatientTransaction);
						}
						if (visitServicePatientTransaction.PatientTransactionDetail?.IsApplyAccommodationClassPricing == true && model.PatientTransactionModel.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured && visitServicePatientTransaction.PatientTransactionDetail.ActualPriceBeforeDiscount.HasValue)
						{
							model.PriceInquiry.CompanyDiscount = model.PatientTransactionModel.ActualCompanyDiscount;
						}
						packageExpiryDate = this._InsuredEngine.GetPackageExpiryDate();
						packageStartDate = this._InsuredEngine.GetPackageStartDate();
					}
				}

				#endregion

				#region 4 : Set Invalid Status and package date and log response
				if (savePatientTransaction && !model.PriceInquiry.IsValid && isEditMode) // save the note valid transaction in edit mode
				{

					visitServicePatientTransaction.StatusCode = model.PriceInquiry.StatusCode;
					visitServicePatientTransaction.MessageResult = model.PriceInquiry.MessageResult;
					visitServicePatientTransaction.Invalid = !model.PriceInquiry.IsValid;

				}
				//KSA Limit Effect In Price Inquiry
				//if (savePatientTransaction && model.PriceInquiry.IsValid && visitServicePatientTransaction.PatientPackageID.HasValue && packageExpiryDate != null || packageStartDate != null)
				if (model.PriceInquiry.IsValid && visitServicePatientTransaction != null && visitServicePatientTransaction.PatientPackageID.HasValue && packageExpiryDate != null || packageStartDate != null)
				{

					PatientTransaction packagePurchasingRecord = _PatientTransactionInMemoryManger.GetPatientTransactionByID(visitServicePatientTransaction.PatientPackageID.Value, patientTransactionsWithExtra, true);
					if (packageExpiryDate.HasValue)
						packagePurchasingRecord.PatientPackage.ExpiryDate = packageExpiryDate;
					if (packageStartDate.HasValue)
						packagePurchasingRecord.PatientPackage.StartDate = packageStartDate;

				}
				if (!savePatientTransaction || (!model.PriceInquiry.IsValid && !isEditMode))
				{
					//KSA Limit Effect In Price Inquiry
					DummyVisitServiceIDs.Add(model.PriceInquiry.VisitServiceID.Value);
					result.Add(model.PriceInquiry);
					if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Cash)
						_CashEngine.LogCashFinalResponse(model.PriceInquiry);
					else if (parameter.FinancialStatus == (int)BillingEnums.FinancialStatus.Insured)
						_InsuredEngine.LogInsuredFinalResponse(model.PriceInquiry);
				}
				#endregion
			}
			if (modifyPatientTrans.Count > 0)
			{
				#region 5 : IF Mode KSA and IF Service Exceed limit 
				if (latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && exceedLimitList.Count >= 0)
				{
					//KSA Limit Effect In Price Inquiry 
					List<PatientTransactionWithExtraData> transactionsToBeRedistributed = patientTransactionsWithExtra.Where(it => it.PatientTransaction.Invalid != true && it.PatientTransaction.PatientTransactionDetail.IsClaim != true && it.PatientTransaction.ID > 0).ToList();
					if (savePatientTransaction == false)
					{
						List<PatientTransactionWithExtraData> transactionsToBeRedistributedForPriceInquiry = patientTransactionsWithExtra.Where(it => it.PatientTransaction.Invalid != true && it.PatientTransaction.PatientTransactionDetail.IsClaim != true && DummyVisitServiceIDs.Contains(it.PatientTransaction.VisitServiceID.Value)).ToList();
						transactionsToBeRedistributed.AddRange(transactionsToBeRedistributedForPriceInquiry);
					}

					_PatientTransactionMngr.ResetPatientTransactionPriceShare(transactionsToBeRedistributed);

					bool isExceed = _InsuredEngine.RedistributePriceShare(transactionsToBeRedistributed);
					if (isExceed)
					{
						exceedLimitList.Add(isExceed);
					}
					editOldPatientTransExceedLimit.AddRange(transactionsToBeRedistributed);

					if (patientTransactionsWithExtra.Count > 0)
					{
						modifyPatientTrans = patientTransactionsWithExtra.Select(pt => pt.PatientTransaction).ToList();
					}

					if (latestConfiguration.MarkServiceExceedLimit == (int)BillingEnums.ConfigMarkServiceExceedLimit.AllServices && exceedLimitList.Count > 0)
					{
						foreach (var item in modifyPatientTrans.Where(t=>t.PatientTransactionDetail.IsClaim != true && t.PatientTransactionDetail.ApprovalStatus == null))
						{
							//if (item.PatientTransactionDetail.IsClaim != true && item.PatientTransactionDetail.ApprovalStatus == null)// item.PatientTransactionDetail.ApprovalStatus != (int)BillingEnums.ApprovalStatus.Approved && item.PatientTransactionDetail.ApprovalStatus != (int)BillingEnums.ApprovalStatus.Rejected)
							//{
								MapPatientTransactionModelKSA( item);

								//bool treatPatient = false;
								//VisitServiceWithCashPrice visitServiceWithCashPrice = _InsuredEngine.GetCashPricePerVisitServiceID(item.VisitServiceID.Value, out treatPatient);
								////  item.PatientShare += item.CompanyShare;
								//if (treatPatient == true)
								//{
								//	item.PatientShare = visitServiceWithCashPrice.CashPrice;
								//	item.Price = item.PatientShare;
								//	item.PatientTransactionDetail.PriceBeforeDiscount = item.PatientTransactionDetail.ActualPriceBeforeDiscount = visitServiceWithCashPrice.CashPrice;
								//	item.PatientTransactionDetail.UnitPriceBeforeDiscount = item.PatientTransactionDetail.PriceBeforeDiscount / item.ProductQuantity;
								//	item.CoveredCompanyDiscount = 0;
								//	item.CoveredCompanyShare = 0;
								//	item.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
								//	item.CoveredPatientShare = 0;
								//	item.CompanyDiscountAmount = item.PatientTransactionDetail.PriceBeforeDiscount - item.Price;
								//	item.CoveredQuantity = 0;
								//	item.NotCoveredQuantity = item.ProductQuantity.Value - item.Inquantity;
								//	item.NotCoveredCompanyDiscount = item.CompanyDiscountAmount;
								//	item.PriceListID = visitServiceWithCashPrice.CashPriceList.ID;
								//	item.PricePlanID = visitServiceWithCashPrice.CashPricePlan.ID;
								//	item.PricePlanRangeSetID = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.ID : null as long?;
								//	item.PricePlanRangeSetRank = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.Rank : null as int?;
								//	item.CompanyDiscount = 0;
								//	item.CoveredPrice = 0;
								//}
								//else if (treatPatient == false)
								//{
								//	item.PatientShare = item.PatientTransactionDetail.PriceBeforeDiscount;
								//	item.Price = item.PatientShare;
								//	item.CoveredCompanyDiscount = 0;
								//	item.CoveredCompanyShare = 0;
								//	item.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
								//	item.CoveredPatientShare = 0;
								//	item.CompanyDiscountAmount = item.PatientTransactionDetail.PriceBeforeDiscount - item.Price;
								//	item.CoveredQuantity = 0;
								//	item.NotCoveredQuantity = item.ProductQuantity.Value - item.Inquantity;
								//	item.NotCoveredCompanyDiscount = item.CompanyDiscountAmount;
								//	item.CompanyDiscount = 0;
								//	item.CoveredPrice = 0;
								//}
								//item.CompanyShare = decimal.Zero;
								//item.PatientTransactionDetail.IsNeedApproval = (int)BillingEnums.IsNeedApproval.Needapproval;

								//========================set tax patient and company shares ===============================================================//
								if (!reInquiry)
								{
									foreach (var tax in item.PatientTransactionTaxes)
									{
										tax.TaxAmount += tax.CompanyTaxAmount;
										tax.TaxAmountDue += tax.CompanyTaxAmount;
										tax.CoveredPatientTaxAmount = tax.CoveredPatientTaxAmount;
										tax.CoveredPatientTaxAmountDue = tax.CoveredPatientTaxAmountDue;
										tax.CompanyTaxAmount = decimal.Zero;
									}
								}
								//==========================================================================================================================//
								if (item.ID > 0)
								{
									editOldPatientTransExceedLimit.Add(new PatientTransactionWithExtraData
									{
										PatientTransaction = item,
										PriceListType = item.Product.ProductType.ToLower() == BillingEnums.Service.ToLower() ? BillingEnums.PriceListType.Service : BillingEnums.PriceListType.Item,
									});
								}
							//}
							//if (item.ID <= 0 && savePatientTransaction)
							//    _InsuredEngine.SavePatientTransaction(item);
						}

					}
					modifyPatientTrans = modifyPatientTrans.Where(a => a.ID <= 0).ToList();
					foreach (var item in modifyPatientTrans)
					{
						_InsuredEngine.SavePatientTransaction(item);
					}
				}
				else
				{
					foreach (var item in modifyPatientTrans)
					{
						if (item.ID <= 0)
							_InsuredEngine.SavePatientTransaction(item);
					}
				}
			}
			#endregion

			#region 6 : if call reInquiry service 
			if (reInquiry) //redistribute
			{
				if (parameters.Count > 0)
				{
					if (parameters.First().FinancialStatus == (int)BillingEnums.FinancialStatus.Insured)
					{
						List<PatientTransactionWithExtraData> transactionsToBeRedistributed = patientTransactionsWithExtra.Where(it => it.PatientTransaction.Invalid != true && it.PatientTransaction.PatientTransactionDetail.IsClaim != true).ToList();
						_PatientTransactionMngr.ResetPatientTransactionPriceShare(transactionsToBeRedistributed);
						bool isExceed = _InsuredEngine.RedistributePriceShare(transactionsToBeRedistributed);
						if (isExceed)
						{
							exceedLimitList.Add(isExceed);
						}
						//    apply pricing limit in case exceeding limit
						if (latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && exceedLimitList.Count > 0 && latestConfiguration.MarkServiceExceedLimit == (int)BillingEnums.ConfigMarkServiceExceedLimit.AllServices)
						{
							foreach (var item in patientTransactionsWithExtra.Where(it => it.PatientTransaction.Invalid != true &&
							it.PatientTransaction.PatientTransactionDetail.IsClaim != true && it.PatientTransaction.PatientTransactionDetail.ApprovalStatus == null).Select(pt => pt.PatientTransaction).ToList())
							{
								//if ()// item.PatientTransactionDetail.ApprovalStatus != (int)BillingEnums.ApprovalStatus.Approved && item.PatientTransactionDetail.ApprovalStatus != (int)BillingEnums.ApprovalStatus.Rejected)
								//{
									MapPatientTransactionModelKSA(item);
                                    //bool treatPatient = false;
                                    //VisitServiceWithCashPrice visitServiceWithCashPrice = _InsuredEngine.GetCashPricePerVisitServiceID(item.VisitServiceID.Value, out treatPatient);
                                    //if (treatPatient == true)
                                    //{
                                    //	item.PatientShare = visitServiceWithCashPrice.CashPrice;
                                    //	item.Price = item.PatientShare;
                                    //	item.PatientTransactionDetail.PriceBeforeDiscount = item.PatientTransactionDetail.ActualPriceBeforeDiscount = visitServiceWithCashPrice.CashPrice;
                                    //	item.PatientTransactionDetail.UnitPriceBeforeDiscount = item.PatientTransactionDetail.PriceBeforeDiscount / item.ProductQuantity;
                                    //	item.CoveredCompanyDiscount = 0;
                                    //	item.CoveredCompanyShare = 0;
                                    //	item.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
                                    //	item.CoveredPatientShare = 0;
                                    //	item.CompanyDiscountAmount = item.PatientTransactionDetail.PriceBeforeDiscount - item.Price;
                                    //	item.CoveredQuantity = 0;
                                    //	item.NotCoveredQuantity = item.ProductQuantity.Value - item.Inquantity;
                                    //	item.NotCoveredCompanyDiscount = item.CompanyDiscountAmount;
                                    //	item.PriceListID = visitServiceWithCashPrice.CashPriceList.ID;
                                    //	item.PricePlanID = visitServiceWithCashPrice.CashPricePlan.ID;
                                    //	item.PricePlanRangeSetID = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.ID : null as long?;
                                    //	item.PricePlanRangeSetRank = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.Rank : null as int?;
                                    //	item.CompanyDiscount = 0;
                                    //	item.CoveredPrice = 0;

                                    //}
                                    //else if (treatPatient == false)
                                    //{
                                    //	item.PatientShare = item.PatientTransactionDetail.PriceBeforeDiscount;
                                    //	item.Price = item.PatientShare;
                                    //	item.CoveredCompanyDiscount = 0;
                                    //	item.CoveredCompanyShare = 0;
                                    //	item.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
                                    //	item.CoveredPatientShare = 0;
                                    //	item.CompanyDiscountAmount = item.PatientTransactionDetail.PriceBeforeDiscount - item.Price;
                                    //	item.CoveredQuantity = 0;
                                    //	item.NotCoveredQuantity = item.ProductQuantity.Value - item.Inquantity;
                                    //	item.NotCoveredCompanyDiscount = item.CompanyDiscountAmount;
                                    //	item.CompanyDiscount = 0;
                                    //	item.CoveredPrice = 0;
                                    //}
                                    //// item.PatientShare += item.CompanyShare;
                                    //item.CompanyShare = decimal.Zero;
                                    //item.PatientTransactionDetail.IsNeedApproval = (int)BillingEnums.IsNeedApproval.Needapproval;

                                    //========================set tax patient and company shares ===============================================================//
                                    foreach (var tax in item.PatientTransactionTaxes)
                                    {
                                        tax.TaxAmount += tax.CompanyTaxAmount;
                                        tax.TaxAmountDue += tax.CompanyTaxAmount;
                                        tax.CoveredPatientTaxAmount = tax.CoveredPatientTaxAmount;
                                        tax.CoveredPatientTaxAmountDue = tax.CoveredPatientTaxAmountDue;
                                        tax.CompanyTaxAmount = decimal.Zero;
                                    }

                                 //   ==========================================================================================================================//

                                //}
							}
						}
					}
					else
					{
						var transactionsToBeRedistributed = patientTransactionsWithExtra.Where(it => it.PatientTransaction.Invalid != true && it.PatientTransaction.PatientPackageID != null).ToList();
						_PatientTransactionMngr.ResetPatientTransactionPriceShare(transactionsToBeRedistributed);
						_CashEngine.RedistributePriceShare(transactionsToBeRedistributed, _CashEngine.GetLogData());
					}
				}

			}
			#endregion
			if (reInquiry || savePatientTransaction)
			{
				_unitOfWork.BulkSaveChanges();
			}
			#region 7 :If Mode KSA Apply Limit Effect In Price Inquiry
			//KSA Limit Effect In Price Inquiry

			var returnedTrans = patientTransactionsWithExtra;//.Where(v=>v.PatientTransaction.VisitServiceID> 0).ToList();
			if (!reInquiry && savePatientTransaction)
			{
				var visitServicesID = parameters.Where(v => v.VisitServiceID > 0).Select(it => it.VisitServiceID).ToList();
				returnedTrans = patientTransactionsWithExtra.Where(it => visitServicesID.Contains(it.PatientTransaction.VisitServiceID)).ToList();
				if (latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && exceedLimitList.Count > 0 && latestConfiguration.MarkServiceExceedLimit == (int)BillingEnums.ConfigMarkServiceExceedLimit.AllServices)
				{//editOldPatientTransExceedLimit
					returnedTrans.AddRange(editOldPatientTransExceedLimit);
				}
				else if (latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && exceedLimitList.Count >= 0 && editOldPatientTransExceedLimit.Count > 0)
				{//editOldPatientTransExceedLimit
					returnedTrans.AddRange(editOldPatientTransExceedLimit);
				}
			}

			//KSA Limit Effect In Price Inquiry
			if (savePatientTransaction == false && returnedTrans.Count > 0 && result.Count > 0)// && returnedTrans.Count >= parameters.Count)
			{
				returnedTrans = returnedTrans.Where(r => r.PatientTransaction.VisitServiceID < 0).ToList();
				if (returnedTrans.Count == result.Count)
					result = new List<PriceInquiryModel>();
				else if (returnedTrans.Count > 0)
				{
					List<long> VisitServiceIDs = returnedTrans.Select(r => r.PatientTransaction.VisitServiceID.Value).ToList();
					result = result.Where(p => !VisitServiceIDs.Contains(p.VisitServiceID.Value)).ToList();
				}
			}
			#endregion

			#region 8 : Set result 
			// Set result for all transactions in case insured
			foreach (var transactionWithExtra in returnedTrans)
			{
				// var transaction = null;
				//  if(modifyPatientTrans.Where(t => t.ID == transactionWithExtra.PatientTransaction.ID) == null)
				//var transaction = modifyPatientTrans.Where(t => t.ID == transactionWithExtra.PatientTransaction.ID).FirstOrDefault();
				//if (transaction == null)
				var transaction = transactionWithExtra.PatientTransaction;
				decimal priceAfterRounding = Math.Round(transaction.Price.Value, 2);
				decimal CompnayShareAfterRounding = Math.Round(transaction.CompanyShare.Value, 2);
				PriceInquiryModel model = new PriceInquiryModel();
				if (transaction.Invalid == true)
				{
					model.StatusCode = transaction.StatusCode.Value;
					model.IsValid = false;
					model.MessageResult = transaction.MessageResult;
					model.TransactionID = transaction.ID;
					model.ProductID = transaction.ProductID;
					//KSA Limit Effect In Price Inquiry
					model.VisitServiceID = transaction.VisitServiceID; // (savePatientTransaction == true) ? transaction.VisitServiceID : ((transaction.VisitServiceID >= 0) ? transaction.VisitServiceID : 0);
					model.PatientShare = null;
					model.CompanyShare = null;
					model.Price = null;
					model.PackageShare = null;
					model.Inquantity = null;
					model.PackageDiscount = null;
					model.IsNeedApproval = 0;
					model.CompanyDiscount = null;
					model.PriceBeforeDiscount = null;
					model.CoveredPriceBeforeDiscount = null;
					model.CoveredPatientShare = null;
					model.CoveredCompanyDiscount = null;
					model.NotCoveredCompanyDiscount = null;
					model.CompanyDiscountAmount = null;
					model.InsuredPriceBeforeDiscount = null;
					model.CoveredQuantity = null;
					model.ContractVisitLimit = null;
					model.ContractTypeID = null;
					model.ContractTypeName = null;
					model.ContractTypeArName = null;
					model.Taxes = new List<HMIS.Billing.Model.Custom.BillingEngine.TaxModel>();
				}
				else
				{
					model.StatusCode = BillingEnums.BillingEngineStatusCode.Valid;
					model.IsValid = true;
					model.MessageResult = string.Format(EngineMessages.RemainingPatientTransaction, transaction.VisitID, transaction.ID);
					model.TransactionID = transaction.ID;
					model.ProductID = transaction.ProductID;
					model.PatientShare = transaction.Inquantity > 0 ? Math.Round(transaction.PatientShare.Value, 2) : priceAfterRounding - CompnayShareAfterRounding;
					model.CompanyShare = CompnayShareAfterRounding;
					model.Price = priceAfterRounding;
					model.PackageShare = transaction.PackageShare.HasValue ? Math.Round(transaction.PackageShare.Value, 2) : null as decimal?;
					model.Inquantity = transaction.Inquantity;
					model.PackageDiscount = transaction.PackageDiscount;
					model.IsNeedApproval = transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured ? transaction.PatientTransactionDetail.IsNeedApproval.Value : 0;
					model.ShowinAuthorization = transaction.PatientTransactionDetail.ShowInAuthorizationScreen;
					model.CompanyDiscount = transaction.CompanyDiscount;
					// partially 
					model.CoveredPriceBeforeDiscount = transaction.PatientTransactionDetail != null ? transaction.PatientTransactionDetail.CoveredPriceBeforeDiscount : transaction.PatientTransactionDetail.PriceBeforeDiscount;
					model.CoveredPatientShare = transaction.CoveredPatientShare;
					model.CoveredCompanyDiscount = transaction.CoveredCompanyDiscount;
					model.NotCoveredCompanyDiscount = transaction.NotCoveredCompanyDiscount;
					model.CompanyDiscountAmount = transaction.CompanyDiscountAmount;
					model.InsuredPriceBeforeDiscount = transaction.InsuredPriceBeforeDiscount;
					model.CoveredQuantity = transaction.CoveredQuantity;
					model.ContractVisitLimit = transaction.ContractVisitLimit;
					model.ContractTypeID = contractTypeInfo.ContractTypeID;
					model.ContractTypeName = contractTypeInfo.ContractTypeName;
					model.ContractTypeArName = contractTypeInfo.ContractTypeArName;
					model.PriceBeforeDiscount = transaction.PatientTransactionDetail != null ? transaction.PatientTransactionDetail.PriceBeforeDiscount : transaction.Price;
					if (transaction.PatientTransactionDetail?.IsApplyAccommodationClassPricing == true && transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured && transaction.PatientTransactionDetail.ActualPriceBeforeDiscount.HasValue)
					{
						model.CompanyDiscount = transaction.ActualCompanyDiscount;
						model.PriceBeforeDiscount = transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured ? transaction.PatientTransactionDetail.ActualPriceBeforeDiscount : transaction.Price;

					}
					if (model.PriceBeforeDiscount.HasValue)
					{
						model.PriceBeforeDiscount = Math.Round(model.PriceBeforeDiscount.Value, 2);
					}
					//KSA Limit Effect In Price Inquiry
					model.VisitServiceID = transaction.VisitServiceID;//(savePatientTransaction == true) ? transaction.VisitServiceID : ((transaction.VisitServiceID >= 0 ) ? transaction.VisitServiceID : 0) ;
																	  //KSA Limit Effect In Price Inquiry
					model.Taxes = transaction.PatientTransactionTaxes != null ? transaction.PatientTransactionTaxes.Where(a => (savePatientTransaction == false && a.IsDeleted == null) || a.IsDeleted == false).Select(a => new TaxModel()
					{
						PatientTaxAmount = Math.Round(a.TaxAmount.Value, 2),
						PatientTaxAmountDue = Math.Round(a.TaxAmountDue.Value, 2),
						TaxID = a.TaxID.Value,
						CompanyTaxAmount = Math.Round(a.CompanyTaxAmount.Value, 2),
						//KSA Limit Effect In Price Inquiry
						PackageTaxAmount = a.PackageTaxAmount.HasValue ? Math.Round(a.PackageTaxAmount.Value, 2) : (savePatientTransaction == false && !a.PackageTaxAmount.HasValue ? 0 : null as decimal?),
						TaxName = a.Tax != null ? a.Tax.EnName : _TaxManager.GetTaxEnNameByID(a.TaxID.Value),
						CoveredPatientTaxAmount = a.CoveredPatientTaxAmount.HasValue ? Math.Round(a.CoveredPatientTaxAmount.Value, 2) : null as decimal?,
						CoveredPatientTaxAmountDue = a.CoveredPatientTaxAmountDue.HasValue ? Math.Round(a.CoveredPatientTaxAmountDue.Value, 2) : null as decimal?

					}).ToList() : new List<HMIS.Billing.Model.Custom.BillingEngine.TaxModel>();
				}

				result.Add(model);
				if (transactionWithExtra.PatientTransaction.FinancialStatusID == (int)BillingEnums.PriceListCategory.Cash)
					_CashEngine.LogCashFinalResponse(model);
				else if (transactionWithExtra.PatientTransaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured)
					_InsuredEngine.LogInsuredFinalResponse(model);
			}
			#endregion
			if (savePatientTransaction == false)
				result = result.OrderByDescending(r => r.VisitServiceID).ToList();
			foreach (PriceInquiryModel item in result)
			{
				item.VisitServiceID = (savePatientTransaction == true) ? item.VisitServiceID : ((item.VisitServiceID >= 0) ? item.VisitServiceID : 0);

			}
			if (savePatientTransaction == false)
				result = result.Where(r => r.VisitServiceID == 0).ToList();

			return result;
		}

		private PriceInquiryModel PriceInquiryProceduresWhileEdit(List<PatientTransactionWithExtraData> patientTransactions, PriceInquiryParameters parameter, bool savePatientTransaction, ref bool hasExceedLimit)
		{
			PriceInquiryModel result = new PriceInquiryModel();
			PatientTransaction patientTransaction = null;
			bool canEditTransaction = true;

			#region Check Null Visit Service ID (IF Null Give VisitServiceID is NULL)

			if (savePatientTransaction && parameter.VisitServiceID == null)
				throw new BillingEngineException("VisitServiceID is NULL", (int)BillingEnums.BillingExceptionMessagesEnum.EmptyVisitServiceID);

			patientTransaction = parameter.VisitServiceID.HasValue ?
				patientTransactions.Where(it => it.PatientTransaction.VisitServiceID == parameter.VisitServiceID.Value).FirstOrDefault().PatientTransaction : null;

			#endregion

			#region Check Null Transaction ID (IF Null Give Transaction does not exist)

			if (patientTransaction == null || patientTransaction.IsDeleted != false || !parameter.TransactionID.HasValue)
				throw new BillingEngineException("Transaction of ID " + parameter.TransactionID.Value + " does not exist", (int)BillingEnums.BillingExceptionMessagesEnum.TransactionIDNotExist);

			#endregion

			#region Reset Price, Price Share And Company Discount to Edited Patient Transaction

			patientTransaction.CompanyShare = decimal.Zero;
			patientTransaction.PatientShare = decimal.Zero;
			patientTransaction.Price = decimal.Zero;
			patientTransaction.ProductQuantity = decimal.Zero;
			patientTransaction.PackageShare = decimal.Zero;
			patientTransaction.Inquantity = decimal.Zero;
			patientTransaction.CoveredPatientShare = null;
			patientTransaction.NotCoveredPatientShare = null;
			patientTransaction.CoveredCompanyShare = null;
			patientTransaction.NotCoveredCompanyShare = null;
			patientTransaction.NotCoveredQuantity = null;
			patientTransaction.CoveredQuantity = null;
			patientTransaction.NotCoveredPrice = null;
			patientTransaction.CoveredPrice = null;
			patientTransaction.InsuredPriceBeforeDiscount = null;
			patientTransaction.CoveredCompanyDiscount = null;
			patientTransaction.NotCoveredCompanyDiscount = null;
			patientTransaction.CompanyDiscountAmount = null;

			#endregion

			#region Check If Requested Product Is Package OR Request Quantity > 1 (IF True Give Error Can't Edit Product Or Quantity)

			if (parameter.PatientPackageID == null && _packageManager.CheckIfProductIsPackage(patientTransaction.ProductID.Value))
			{
				if (patientTransaction.ProductID.Value != parameter.ProductID.Value || parameter.ProductQuantity.Value != 1)
				{
					canEditTransaction = false;
					result = new PriceInquiryModel()
					{
						StatusCode = BillingEnums.BillingEngineStatusCode.CantEditProductOrQuantity,
						IsValid = false,
						MessageResult = EngineMessages.CantEditProductOrQuantity,
						TransactionID = parameter.TransactionID,
						VisitServiceID = parameter.VisitServiceID
					};
				}

				if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Cash)
					_CashEngine.LogCashFinalResponse(result);
				else if (parameter.FinancialStatus == (int)BillingEnums.FinancialStatus.Insured)
					_InsuredEngine.LogInsuredFinalResponse(result);
			}

			#endregion

			#region Can Edit Transaction

			if (canEditTransaction)
			{
				#region Check Financial Status In Current Request

				// Check financial status in current request
				if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Cash)
				{
					#region Call Cash Price Inquiry

					PriceInquiryTransactionalModel model = _CashEngine.CashPriceInquiry(parameter, savePatientTransaction);

					#endregion

					if (savePatientTransaction && model.PriceInquiry.IsValid)
					{
						model.PatientTransactionModel.ID = patientTransaction.ID;

						#region IF Valid Request Update Patient Transaction

						patientTransaction = _CashEngine.UpdatePatientTransaction(model.PatientTransactionModel, patientTransaction);

						#endregion

						DateTime? packageExpiryDate = this._CashEngine.GetPackageExpiryDate();
						DateTime? packageStartDate = this._CashEngine.GetPackageStartDate();

						if (packageExpiryDate != null || packageStartDate != null)
						{
							PatientTransaction packagePurchasingRecord = _PatientTransactionInMemoryManger.GetPatientTransactionByID(patientTransaction.PatientPackageID.Value, patientTransactions, true);
							if (packageExpiryDate.HasValue)
								packagePurchasingRecord.PatientPackage.ExpiryDate = packageExpiryDate;
							if (packageStartDate.HasValue)
								packagePurchasingRecord.PatientPackage.StartDate = packageStartDate;
						}
						model.PriceInquiry.TransactionID = patientTransaction.ID;

						_CashEngine.LogCashFinalResponse(model.PriceInquiry);
					}
					result = model.PriceInquiry;
				}
				else if (parameter.FinancialStatus == (int)BillingEnums.FinancialStatus.Insured)
				{
					#region Call Insured Price Inquiry

					PriceInquiryTransactionalModel model = _InsuredEngine.InsuredPriceInquiry(parameter, savePatientTransaction);

					#endregion

					if (savePatientTransaction && model.PriceInquiry.IsValid)
					{
						if (model.PatientTransactionModel.PatientTransactionDetailModel.IsExceedLimit == true)
							hasExceedLimit = true;
						model.PatientTransactionModel.ID = patientTransaction.ID;

						#region IF Valid Request Update Patient Transaction

						patientTransaction = _InsuredEngine.UpdatePatientTransaction(model.PatientTransactionModel, patientTransaction);

						#endregion

						DateTime? packageExpiryDate = this._InsuredEngine.GetPackageExpiryDate();
						DateTime? packageStartDate = this._InsuredEngine.GetPackageStartDate();

						if (packageExpiryDate != null || packageStartDate != null)
						{
							PatientTransaction packagePurchasingRecord = _PatientTransactionInMemoryManger.GetPatientTransactionByID(patientTransaction.PatientPackageID.Value, patientTransactions, true);
							if (packageExpiryDate.HasValue)
								packagePurchasingRecord.PatientPackage.ExpiryDate = packageExpiryDate;
							if (packageStartDate.HasValue)
								packagePurchasingRecord.PatientPackage.StartDate = packageStartDate;
						}


						model.PriceInquiry.TransactionID = patientTransaction.ID;
						model.PriceInquiry.IsApplyAccommodationClassPricing = patientTransaction.PatientTransactionDetail.IsApplyAccommodationClassPricing;
						model.PriceInquiry.ActualCompanyDiscount = patientTransaction.ActualCompanyDiscount;
						_InsuredEngine.LogInsuredFinalResponse(model.PriceInquiry);

					}
					result = model.PriceInquiry;
				}
				else
					result = new PriceInquiryModel()
					{
						StatusCode = BillingEnums.BillingEngineStatusCode.InvalidFinancialStatus,
						IsValid = false,
						MessageResult = EngineMessages.InvalidFinancialStatus,
						TransactionID = parameter.TransactionID,
						VisitServiceID = parameter.VisitServiceID
					};

				#endregion
			}

			#endregion

			if (!result.IsValid) // Save error in edited record
			{
				patientTransaction.StatusCode = result.StatusCode;
				patientTransaction.MessageResult = result.MessageResult;
				patientTransaction.Invalid = !result.IsValid;
				patientTransaction.ProductQuantity = decimal.Zero;
				patientTransaction.PatientShare = decimal.Zero;
				patientTransaction.CompanyShare = decimal.Zero;
				patientTransaction.Inquantity = decimal.Zero;
				patientTransaction.PackageShare = decimal.Zero;
				patientTransaction.Price = decimal.Zero;
				patientTransaction.CoveredPatientShare = null;
				patientTransaction.NotCoveredPatientShare = null;
				patientTransaction.CoveredCompanyShare = null;
				patientTransaction.NotCoveredCompanyShare = null;
				patientTransaction.NotCoveredQuantity = null;
				patientTransaction.CoveredQuantity = null;
				patientTransaction.NotCoveredPrice = null;
				patientTransaction.CoveredPrice = null;
				patientTransaction.InsuredPriceBeforeDiscount = null;
				patientTransaction.CoveredCompanyDiscount = null;
				patientTransaction.NotCoveredCompanyDiscount = null;
				patientTransaction.CompanyDiscountAmount = null;
			}

			return result;
		}

		public List<PriceInquiryModel> CancelPatientTransaction(List<long> transactionIds)
		{
			#region Validate Request (If There Is No I/p Transactions Give Invalid Request)

			List<PriceInquiryModel> result = new List<PriceInquiryModel>();
			if (transactionIds == null || transactionIds.Count == 0)
			{
				result.Add(new PriceInquiryModel()
				{
					StatusCode = BillingEnums.BillingEngineStatusCode.InvalidRequest,
					IsValid = false,
					MessageResult = EngineMessages.InvalidRequest
				});
				return result;
			}

			#endregion

			#region Validate First Transaction ID IF NULL Give Transaction Dose Not Exist)

			PatientTransaction patientTransaction = _PatientTransactionMngr.GetPatientTransactionByIDNotDeleted(transactionIds.FirstOrDefault());

			if (patientTransaction == null)
			{
				throw new BillingEngineException("Transaction of ID " + transactionIds.FirstOrDefault() + " does not exist", (int)BillingEnums.BillingExceptionMessagesEnum.TransactionIDNotExist);
			}

			#endregion

			#region IF Cash Call Cash Cancel Patient Transaction 

			else if (patientTransaction.FinancialStatusID == (int)BillingEnums.PriceListCategory.Cash)
			{
				return _CashEngine.CancelPatientTransaction(transactionIds, patientTransaction.VisitID.Value);
			}

			#endregion

			#region If Insured Call Insured Cancel Patient Transaction

			else if (patientTransaction.FinancialStatusID == (int)BillingEnums.PriceListCategory.Insured)
			{
				return _InsuredEngine.CancelPatientTransaction(transactionIds, patientTransaction.VisitID.Value);
			}

			#endregion

			return result;
		}

		public List<PriceInquiryModel> EditPatientTransaction(PriceInquiryParameters parameter)
		{
			List<PriceInquiryModel> result = new List<PriceInquiryModel>();
			ContractTypeInfo contractTypeInfo = new ContractTypeInfo();
			var latestConfiguration = _ConfigurationMngr.GetLastActiveConfiguration();

			bool editTransactionHasExceedLimit = false;
			bool anyTransactionHasExceedLimit = false;

			#region Validate Null Parameter Request (IF Null give invalid request)

			if (parameter == null)
			{
				result.Add(new PriceInquiryModel()
				{
					StatusCode = BillingEnums.BillingEngineStatusCode.InvalidRequest,
					IsValid = false,
					MessageResult = EngineMessages.InvalidRequest
				});
				return result;
			}

			#endregion

			#region Validate Transaction ID Parameter (IF Null give invalid transaction ID)

			else if (parameter.TransactionID == null)
			{
				result.Add(new PriceInquiryModel()
				{
					StatusCode = BillingEnums.BillingEngineStatusCode.InvalidTransactionID,
					IsValid = false,
					MessageResult = EngineMessages.InvalidTransactionID,
					TransactionID = parameter.TransactionID,
					VisitServiceID = parameter.VisitServiceID
				});
				return result;
			}

			#endregion

			TransactionOptions options = new TransactionOptions();
			options.IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted;
			options.Timeout = TimeSpan.FromMinutes(transactionTimeOut);
			using (TransactionScope ts = new TransactionScope(TransactionScopeOption.Required, options, TransactionScopeAsyncFlowOption.Enabled))
			{
				try
				{
					#region Get remaining transaction of the visit after and before edited record

					// Get remaining transaction of the visit after edited record
					List<PatientTransactionWithExtraData> patientTransactions = _PatientTransactionMngr.GetPatientTransactionsByVisitIDWithExtraDetails(parameter.VisitID.Value).ToList();
					List<PatientTransactionWithExtraData> patientTransactionsBefore = patientTransactions.Where(a => a.PatientTransaction.ID < parameter.TransactionID.Value).ToList();
					List<PatientTransactionWithExtraData> toBeResetedBefore = patientTransactionsBefore;

					List<PatientTransactionWithExtraData> patientTransactionsAfterEdit = patientTransactions.Where(a => a.PatientTransaction.ID > parameter.TransactionID.Value).ToList();
					List<PatientTransactionWithExtraData> toBeReseted = patientTransactionsAfterEdit;

					#endregion

					#region IF Cash Request (Intialize data like get billing configuration, active taxes, etc...)

					if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Cash)
					{
						_CashEngine.IntializeEngine(new EngineProcedureCacheData
						{
							PatientTransactionWithExtraData = patientTransactions,
							EpisodeType = parameter.EpisodeType ?? 0
						});
					}

					#endregion

					#region IF Insured Request (Intialize data like get contract, cover letter, insurance card, contractor, etc...)

					else
					{
						List<long> allContracts = patientTransactions.Where(it => it.PatientTransaction.ContractID.HasValue).Select(it => it.PatientTransaction.ContractID.Value).Distinct().ToList();
						if (parameter.ContractID.HasValue) allContracts.Add(parameter.ContractID.Value);
						List<long> allCoverLetters = patientTransactions.Where(it => it.PatientTransaction.CoverLetterID.HasValue).Select(it => it.PatientTransaction.CoverLetterID.Value).Distinct().ToList();
						if (parameter.CoverLetterID.HasValue) allCoverLetters.Add(parameter.CoverLetterID.Value);
						List<long> allInsuranceCards = patientTransactions.Where(it => it.PatientTransaction.InsuranceCardID.HasValue).Select(it => it.PatientTransaction.InsuranceCardID.Value).Distinct().ToList();
						if (parameter.InsuranceCardID.HasValue) allInsuranceCards.Add(parameter.InsuranceCardID.Value);
						List<long> allContractors = patientTransactions.Where(it => it.PatientTransaction.ContractorID.HasValue).Select(it => it.PatientTransaction.ContractorID.Value).Distinct().ToList();
						if (parameter.ContractorID.HasValue) allInsuranceCards.Add(parameter.ContractorID.Value);

						_InsuredEngine.IntializeEngine(new EngineProcedureCacheData
						{
							PatientTransactionWithExtraData = patientTransactions,
							ToBeLoadedInsuranceCards = allInsuranceCards.Distinct().ToList(),
							ToBeLoadedContracts = allContracts.Distinct().ToList(),
							ToBeLoadedCoverLetters = allCoverLetters.Distinct().ToList(),
							ToBeLoadedContractors = allContractors.Distinct().ToList(),

							EpisodeType = parameter.EpisodeType ?? 0
						});

					}

					#endregion

					#region If Cash Request Remove Patient Transactions That Assign To Package From Patient Transactions After Edit (because we will reset all price shares in this transactions)

					if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Cash && patientTransactionsAfterEdit.Count > 0)
						toBeReseted = toBeReseted.Where(a => a.PatientTransaction.PatientPackageID != null).ToList();

					#endregion

					#region Reset All Price Shares To Not Claimed Patient Transactions After Edit

					_PatientTransactionMngr.ResetPatientTransactionPriceShare(toBeReseted.Where(pt => pt.PatientTransaction.PatientTransactionDetail.IsClaim != true).ToList());

					#endregion

					//// before
					//// reset shares
					//// exclude climed visit service from patient transaction in case insured 
					//// Redistribute patient transactions before edit
					//if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Insured && latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true)
					//{
					//    _PatientTransactionMngr.ResetPatientTransactionPriceShare(toBeResetedBefore.Where(pt => pt.PatientTransaction.PatientTransactionDetail.IsClaim != true).ToList());
					//    _InsuredEngine.RedistributePriceShare(patientTransactions.Where(a => a.PatientTransaction.ID < parameter.TransactionID && a.PatientTransaction.PatientTransactionDetail.IsClaim != true).OrderBy(p => p.PatientTransaction.VisitServiceID).ToList());
					//}

					#region Get Edited Patient Transaction Record By Patient Transaction ID

					// edit patient transaction 
					var transactionToBeEdited = patientTransactions.Where(it => it.PatientTransaction.ID == parameter.TransactionID.Value).FirstOrDefault().PatientTransaction;

					#endregion

					#region Check If Package Unassign In Request (old edited record has patient package value and new one in request has no patient package value)

					//check if package unassign
					bool isPackageUnassignFromFirstPerforming = false;
					long? oldPatientPackageID = transactionToBeEdited.PatientPackageID;
					if (transactionToBeEdited.PatientPackageID != null && parameter.PatientPackageID == null)
					{
						List<long> packagePatientTransactionsIds = _PatientTransactionMngr.GetPatientTransactions()
																									 .Where(a => a.PatientPackageID == oldPatientPackageID.Value && a.Inquantity > 0).Select(it => it.ID)
																									 .ToList();
						if (packagePatientTransactionsIds != null && packagePatientTransactionsIds.Count > 0 && parameter.TransactionID == packagePatientTransactionsIds[0])
						{
							isPackageUnassignFromFirstPerforming = true;
						}
					}

					#endregion

					#region Price Inquiry Procedures While Edit

					PriceInquiryModel editedPatientTransactionPriceInquiryModel = PriceInquiryProceduresWhileEdit(patientTransactions, parameter, true, ref editTransactionHasExceedLimit);

					#endregion

					contractTypeInfo.ContractTypeID = editedPatientTransactionPriceInquiryModel.ContractTypeID;
					contractTypeInfo.ContractTypeName = editedPatientTransactionPriceInquiryModel.ContractTypeName;
					contractTypeInfo.ContractTypeArName = editedPatientTransactionPriceInquiryModel.ContractTypeArName;
					if (!editedPatientTransactionPriceInquiryModel.IsValid)
					{
						if (editedPatientTransactionPriceInquiryModel.StatusCode == BillingEnums.BillingEngineStatusCode.CantEditProductOrQuantity)
						{
							result = new List<PriceInquiryModel>();
							result.Add(new PriceInquiryModel()
							{
								StatusCode = BillingEnums.BillingEngineStatusCode.CantEditProductOrQuantity,
								IsValid = false,
								MessageResult = EngineMessages.CantEditProductOrQuantity,
								TransactionID = parameter.TransactionID,
								VisitServiceID = parameter.VisitServiceID,
								ProductID = parameter.ProductID
							});
							return result;
						}
						else if (editedPatientTransactionPriceInquiryModel.StatusCode == BillingEnums.BillingEngineStatusCode.UnExpectedError)
							throw new BillingEngineException(editedPatientTransactionPriceInquiryModel.MessageResult, (int)BillingEnums.BillingExceptionMessagesEnum.FrameworkExceptions);
					}

					#region Reset Price Shares And Redestribute To Before Edited Patient Transaction Record
					// before
					// reset shares
					// exclude claimed visit service from patient transaction in case insured 
					// Redistribute patient transactions before edit
					if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Insured && latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true)
					{
						_PatientTransactionMngr.ResetPatientTransactionPriceShare(toBeResetedBefore.Where(pt => pt.PatientTransaction.PatientTransactionDetail.IsClaim != true).ToList());
						_InsuredEngine.RedistributePriceShare(patientTransactions.Where(a => a.PatientTransaction.ID < parameter.TransactionID && a.PatientTransaction.PatientTransactionDetail.IsClaim != true).OrderBy(p => p.PatientTransaction.VisitServiceID).ToList());
					}

					#endregion

					#region Redestribute To After Edited Patient Transaction Record

					// after
					// exclude claimed visit service from patient transaction in case insured 
					// Redistribute patient transactions after edit
					if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Insured)
						anyTransactionHasExceedLimit = _InsuredEngine.RedistributePriceShare(patientTransactionsAfterEdit.Where(pt => pt.PatientTransaction.PatientTransactionDetail.IsClaim != true).OrderBy(p => p.PatientTransaction.VisitServiceID).ToList());
					else if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Cash)
						_CashEngine.RedistributePriceShare(patientTransactionsAfterEdit, _CashEngine.GetLogData());

					#endregion

					#region In Case Of Unassign Package From Transaction

					// In case of unassign package from transaction
					if (isPackageUnassignFromFirstPerforming)
					{
						#region Validate old Purchased Package (IF NULL Give Transaction Dosenot Exist)

						PatientTransaction packagePurchasingRecord = _PatientTransactionInMemoryManger.PackagesharedMethod(oldPatientPackageID.Value, patientTransactions);



						//  PatientTransaction packagePurchasingRecord = _PatientTransactionMngr.GetPatientTransactionByID(oldPatientPackageID.Value);
						if (packagePurchasingRecord == null || (packagePurchasingRecord != null && packagePurchasingRecord.PatientPackage == null))
							throw new BillingEngineException("Transaction of ID " + packagePurchasingRecord.ID + " does not exist", (int)BillingEnums.BillingExceptionMessagesEnum.TransactionIDNotExist);

						#endregion

						//#region If Package Time Limit Start From First Performing Set Start And Expiry Date

						//if (packagePurchasingRecord.PatientPackage.IsTimeLimit == true && packagePurchasingRecord.PatientPackage.IsStartFromPurchasingDate == false)
						//{
						//    PatientTransaction firstPerformingPatientTransaction = _PatientTransactionInMemoryManger.GetPackageFirstPerformingTransaction(oldPatientPackageID.Value, patientTransactions);


						//    if (firstPerformingPatientTransaction != null)
						//    {
						//        packagePurchasingRecord.PatientPackage.StartDate = firstPerformingPatientTransaction.PerformingDate.Value.Date;
						//        packagePurchasingRecord.PatientPackage.ExpiryDate = _PatientTransactionMngr.CaluclateExpiryDate(packagePurchasingRecord.PatientPackage.PackageExpireAfter.Value, packagePurchasingRecord.PatientPackage.TimeUnitID.Value, firstPerformingPatientTransaction.PerformingDate.Value.Date);
						//    }
						//    else
						//    {
						//        packagePurchasingRecord.PatientPackage.StartDate = null;
						//        packagePurchasingRecord.PatientPackage.ExpiryDate = null;
						//    }
						//}

						//#endregion
					}

					#endregion

					#region Exceed Limit Handle

					if ((anyTransactionHasExceedLimit || editTransactionHasExceedLimit) && latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && latestConfiguration.MarkServiceExceedLimit == (int)BillingEnums.ConfigMarkServiceExceedLimit.AllServices)
					{
						foreach (var item in patientTransactions.Where(it => it.PatientTransaction.Invalid != true && it.PatientTransaction.PatientTransactionDetail.IsClaim != true 
						        && it.PatientTransaction.PatientTransactionDetail.ApprovalStatus == null).Select(pt => pt.PatientTransaction).ToList())
						{
							//if (item.PatientTransactionDetail.IsClaim != true && item.PatientTransactionDetail.ApprovalStatus == null)
							//{
								MapPatientTransactionModelKSA(item);

                                //bool treatPatient = false;
                                //VisitServiceWithCashPrice visitServiceWithCashPrice = _InsuredEngine.GetCashPricePerVisitServiceID(item.VisitServiceID.Value, out treatPatient);
                                //if (treatPatient == true)
                                //{
                                //	item.PatientShare = visitServiceWithCashPrice.CashPrice;
                                //	item.Price = item.PatientShare;
                                //	item.PatientTransactionDetail.PriceBeforeDiscount = item.PatientTransactionDetail.ActualPriceBeforeDiscount = visitServiceWithCashPrice.CashPrice;
                                //	item.PatientTransactionDetail.UnitPriceBeforeDiscount = item.PatientTransactionDetail.PriceBeforeDiscount / item.ProductQuantity;
                                //	item.CoveredCompanyDiscount = 0;
                                //	item.CoveredCompanyShare = 0;
                                //	item.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
                                //	item.CoveredPatientShare = 0;
                                //	item.CompanyDiscountAmount = item.PatientTransactionDetail.PriceBeforeDiscount - item.Price;
                                //	item.CoveredQuantity = 0;
                                //	item.NotCoveredQuantity = item.ProductQuantity.Value - item.Inquantity;
                                //	item.NotCoveredCompanyDiscount = item.CompanyDiscountAmount;
                                //	item.PriceListID = visitServiceWithCashPrice.CashPriceList.ID;
                                //	item.PricePlanID = visitServiceWithCashPrice.CashPricePlan.ID;
                                //	item.PricePlanRangeSetID = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.ID : null as long?;
                                //	item.PricePlanRangeSetRank = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.Rank : null as int?;
                                //	item.CompanyDiscount = 0;
                                //	item.CoveredPrice = 0;
                                //}
                                //else if (treatPatient == false)
                                //{
                                //	item.PatientShare = item.PatientTransactionDetail.PriceBeforeDiscount;
                                //	item.Price = item.PatientShare;
                                //	item.CoveredCompanyDiscount = 0;
                                //	item.CoveredCompanyShare = 0;
                                //	item.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
                                //	item.CoveredPatientShare = 0;
                                //	item.CompanyDiscountAmount = item.PatientTransactionDetail.PriceBeforeDiscount - item.Price;
                                //	item.CoveredQuantity = 0;
                                //	item.NotCoveredQuantity = item.ProductQuantity.Value - item.Inquantity;
                                //	item.NotCoveredCompanyDiscount = item.CompanyDiscountAmount;
                                //	item.CompanyDiscount = 0;
                                //	item.CoveredPrice = 0;
                                //}
                                ////  item.PatientShare += item.CompanyShare;
                                //item.CompanyShare = decimal.Zero;
                                //item.PatientTransactionDetail.IsNeedApproval = (int)BillingEnums.IsNeedApproval.Needapproval;

                                ////========================set tax patient and company shares ===============================================================//
                                foreach (var tax in item.PatientTransactionTaxes)
                                {
                                    tax.TaxAmount += tax.CompanyTaxAmount;
                                    tax.TaxAmountDue += tax.CompanyTaxAmount;
                                    tax.CoveredPatientTaxAmount = tax.CoveredPatientTaxAmount;
                                    tax.CoveredPatientTaxAmountDue = tax.CoveredPatientTaxAmountDue;
                                    tax.CompanyTaxAmount = decimal.Zero;
                                }
                                //==========================================================================================================================//
                          //  }
                        }
					}

					#endregion

					#region Get Remaining Patient Transactions Before Edit And Add It To Return Model

					// Get remaining patient transactions before edit
					List<PatientTransaction> patientTransactionsBeforeEdit = patientTransactions.Where(a => a.PatientTransaction.ID < parameter.TransactionID.Value).Select(it => it.PatientTransaction).ToList();

					// add before to return model 
					foreach (var transaction in patientTransactionsBeforeEdit)
					{
						decimal priceAfterRounding = Math.Round(transaction.Price.Value, 2);
						decimal CompnayShareAfterRounding = Math.Round(transaction.CompanyShare.Value, 2);
						PriceInquiryModel model = new PriceInquiryModel();
						if (transaction.Invalid == true)
						{
							model.StatusCode = transaction.StatusCode.Value;
							model.IsValid = false;
							model.MessageResult = transaction.MessageResult;
							model.TransactionID = transaction.ID;
							model.ProductID = transaction.ProductID;
							model.VisitServiceID = transaction.VisitServiceID;
							model.PatientShare = null;
							model.CompanyShare = null;
							model.Price = null;
							model.PackageShare = null;
							model.Inquantity = null;
							model.PackageDiscount = null;
							model.IsNeedApproval = 0;
							model.ShowinAuthorization = null;
							model.CompanyDiscount = null;
							model.PriceBeforeDiscount = null;
							model.CoveredPriceBeforeDiscount = null;
							model.CoveredPatientShare = null;
							model.CoveredCompanyDiscount = null;
							model.NotCoveredCompanyDiscount = null;
							model.CompanyDiscountAmount = null;
							model.InsuredPriceBeforeDiscount = null;
							model.CoveredQuantity = null;
							model.ContractVisitLimit = null;
							model.ContractTypeID = null;
							model.ContractTypeName = null;
							model.ContractTypeArName = null;
							model.Taxes = new List<HMIS.Billing.Model.Custom.BillingEngine.TaxModel>();
						}
						else
						{
							model.StatusCode = BillingEnums.BillingEngineStatusCode.Valid;
							model.IsValid = true;
							model.MessageResult = string.Format(EngineMessages.RemainingPatientTransaction, transaction.VisitID, transaction.ID);
							model.TransactionID = transaction.ID;
							model.ProductID = transaction.ProductID;
							model.VisitServiceID = transaction.VisitServiceID;
							model.PatientShare = transaction.Inquantity > 0 ? Math.Round(transaction.PatientShare.Value, 2) : priceAfterRounding - CompnayShareAfterRounding;
							model.CompanyShare = CompnayShareAfterRounding;
							model.Price = priceAfterRounding;
							model.PackageShare = transaction.PackageShare.HasValue ? Math.Round(transaction.PackageShare.Value, 2) : null as decimal?;
							model.Inquantity = transaction.Inquantity;
							model.PackageDiscount = transaction.PackageDiscount;
							model.IsNeedApproval = transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured ? transaction.PatientTransactionDetail.IsNeedApproval.Value : 0;
							model.ShowinAuthorization = transaction.PatientTransactionDetail.ShowInAuthorizationScreen;
							model.CompanyDiscount = transaction.CompanyDiscount;
							// partially 
							model.CoveredPriceBeforeDiscount = transaction.PatientTransactionDetail != null ? transaction.PatientTransactionDetail.CoveredPriceBeforeDiscount : transaction.PatientTransactionDetail.PriceBeforeDiscount;
							model.CoveredPatientShare = transaction.CoveredPatientShare;
							model.CoveredCompanyDiscount = transaction.CoveredCompanyDiscount;
							model.NotCoveredCompanyDiscount = transaction.NotCoveredCompanyDiscount;
							model.CompanyDiscountAmount = transaction.CompanyDiscountAmount;
							model.InsuredPriceBeforeDiscount = transaction.InsuredPriceBeforeDiscount;
							model.CoveredQuantity = transaction.CoveredQuantity;
							model.ContractVisitLimit = transaction.ContractVisitLimit;
							model.ContractTypeID = contractTypeInfo.ContractTypeID;
							model.ContractTypeName = contractTypeInfo.ContractTypeName;
							model.ContractTypeArName = contractTypeInfo.ContractTypeArName;
							model.PriceBeforeDiscount = transaction.PatientTransactionDetail != null ? transaction.PatientTransactionDetail.PriceBeforeDiscount : transaction.Price;
							if (transaction.PatientTransactionDetail?.IsApplyAccommodationClassPricing == true && transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured && transaction.PatientTransactionDetail.ActualPriceBeforeDiscount.HasValue)
							{
								model.CompanyDiscount = transaction.ActualCompanyDiscount;
								model.PriceBeforeDiscount = transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured ? transaction.PatientTransactionDetail.ActualPriceBeforeDiscount : transaction.Price;

							}
							if (model.PriceBeforeDiscount.HasValue)
							{
								model.PriceBeforeDiscount = Math.Round(model.PriceBeforeDiscount.Value, 2);
							}
							model.Taxes = transaction.PatientTransactionTaxes != null ? transaction.PatientTransactionTaxes.Where(a => a.IsDeleted == false).Select(a => new TaxModel()
							{
								PatientTaxAmount = Math.Round(a.TaxAmount.Value, 2),
								PatientTaxAmountDue = Math.Round(a.TaxAmountDue.Value, 2),
								TaxID = a.TaxID.Value,
								CompanyTaxAmount = Math.Round(a.CompanyTaxAmount.Value, 2),
								PackageTaxAmount = a.PackageTaxAmount.HasValue ? Math.Round(a.PackageTaxAmount.Value, 2) : null as decimal?,
								TaxName = a.Tax != null ? a.Tax.EnName : _TaxManager.GetTaxEnNameByID(a.TaxID.Value),
								CoveredPatientTaxAmount = a.CoveredPatientTaxAmount.HasValue ? Math.Round(a.CoveredPatientTaxAmount.Value, 2) : null as decimal?,
								CoveredPatientTaxAmountDue = a.CoveredPatientTaxAmountDue.HasValue ? Math.Round(a.CoveredPatientTaxAmountDue.Value, 2) : null as decimal?,

							}).ToList() : new List<HMIS.Billing.Model.Custom.BillingEngine.TaxModel>();
						}

						result.Add(model);
						if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Cash)
							_CashEngine.LogCashFinalResponse(model);
						else if (parameter.FinancialStatus == (int)BillingEnums.PriceListCategory.Insured)
							_InsuredEngine.LogInsuredFinalResponse(model);
					}

					#endregion

					#region Handle Exceed Limit In Edited Patient Transaction

					PatientTransaction editPatientTransaction = patientTransactions.Where(it => it.PatientTransaction.ID == parameter.TransactionID.Value).FirstOrDefault().PatientTransaction;

					if ((anyTransactionHasExceedLimit || editTransactionHasExceedLimit) && latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && latestConfiguration.MarkServiceExceedLimit == (int)BillingEnums.ConfigMarkServiceExceedLimit.AllServices && editPatientTransaction.PatientTransactionDetail?.ApprovalStatus == null)
					{
						//MapPatientTransactionModelKSA(editPatientTransaction);
						bool treatPatient = false;
						VisitServiceWithCashPrice visitServiceWithCashPrice = _InsuredEngine.GetCashPricePerVisitServiceID(editPatientTransaction.VisitServiceID.Value, out treatPatient);
						if (treatPatient == true)
						{
							editPatientTransaction.PatientShare = visitServiceWithCashPrice.CashPrice;
							editPatientTransaction.Price = editPatientTransaction.PatientShare;
							editPatientTransaction.PatientTransactionDetail.PriceBeforeDiscount = editPatientTransaction.PatientTransactionDetail.ActualPriceBeforeDiscount = visitServiceWithCashPrice.CashPrice;
							editPatientTransaction.PatientTransactionDetail.UnitPriceBeforeDiscount = editPatientTransaction.PatientTransactionDetail.PriceBeforeDiscount / editPatientTransaction.ProductQuantity;
							editPatientTransaction.CoveredCompanyDiscount = 0;
							editPatientTransaction.CoveredCompanyShare = 0;
							editPatientTransaction.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
							editPatientTransaction.CoveredPatientShare = 0;
							editPatientTransaction.CompanyDiscountAmount = editPatientTransaction.PatientTransactionDetail.PriceBeforeDiscount - editPatientTransaction.Price;
							editPatientTransaction.CoveredQuantity = 0;
							editPatientTransaction.NotCoveredQuantity = editPatientTransaction.ProductQuantity.Value - editPatientTransaction.Inquantity;
							editPatientTransaction.NotCoveredCompanyDiscount = editPatientTransaction.CompanyDiscountAmount;
							editPatientTransaction.PriceListID = visitServiceWithCashPrice.CashPriceList.ID;
							editPatientTransaction.PricePlanID = visitServiceWithCashPrice.CashPricePlan.ID;
							editPatientTransaction.PricePlanRangeSetID = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.ID : null as long?;
							editPatientTransaction.PricePlanRangeSetRank = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.Rank : null as int?;
							editPatientTransaction.CompanyDiscount = 0;
							editPatientTransaction.CoveredPrice = 0;
							//--------------------------------------------------------------------
							editedPatientTransactionPriceInquiryModel.PatientShare = visitServiceWithCashPrice.CashPrice;
							editedPatientTransactionPriceInquiryModel.Price = editPatientTransaction.PatientShare;
							editedPatientTransactionPriceInquiryModel.PriceBeforeDiscount = editPatientTransaction.PatientTransactionDetail.ActualPriceBeforeDiscount = visitServiceWithCashPrice.CashPrice;
							editedPatientTransactionPriceInquiryModel.CoveredCompanyDiscount = 0;
							editedPatientTransactionPriceInquiryModel.CoveredPriceBeforeDiscount = 0;
							editedPatientTransactionPriceInquiryModel.CoveredPatientShare = 0;
							editedPatientTransactionPriceInquiryModel.CompanyDiscountAmount = editPatientTransaction.PatientTransactionDetail.PriceBeforeDiscount - editPatientTransaction.Price;
							editedPatientTransactionPriceInquiryModel.CoveredQuantity = 0;
							editedPatientTransactionPriceInquiryModel.NotCoveredCompanyDiscount = editPatientTransaction.CompanyDiscountAmount;
							editedPatientTransactionPriceInquiryModel.CompanyDiscount = 0;

						}
						else if (treatPatient == false)
						{
							editPatientTransaction.PatientShare = editPatientTransaction.PatientTransactionDetail.PriceBeforeDiscount;
							editPatientTransaction.Price = editPatientTransaction.PatientShare;
							editPatientTransaction.CoveredCompanyDiscount = 0;
							editPatientTransaction.CoveredCompanyShare = 0;
							editPatientTransaction.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
							editPatientTransaction.CoveredPatientShare = 0;
							editPatientTransaction.CompanyDiscountAmount = editPatientTransaction.PatientTransactionDetail.PriceBeforeDiscount - editPatientTransaction.Price;
							editPatientTransaction.CoveredQuantity = 0;
							editPatientTransaction.NotCoveredQuantity = editPatientTransaction.ProductQuantity.Value - editPatientTransaction.Inquantity;
							editPatientTransaction.NotCoveredCompanyDiscount = editPatientTransaction.CompanyDiscountAmount;
							editPatientTransaction.CompanyDiscount = 0;
							editPatientTransaction.CoveredPrice = 0;
							//-------------------------------------------------------
							editedPatientTransactionPriceInquiryModel.PatientShare = editPatientTransaction.PatientTransactionDetail.PriceBeforeDiscount;
							editedPatientTransactionPriceInquiryModel.Price = editPatientTransaction.PatientShare;
							editedPatientTransactionPriceInquiryModel.CoveredCompanyDiscount = 0;
							editedPatientTransactionPriceInquiryModel.CoveredPriceBeforeDiscount = 0;
							editedPatientTransactionPriceInquiryModel.CoveredPatientShare = 0;
							editedPatientTransactionPriceInquiryModel.CompanyDiscountAmount = editPatientTransaction.PatientTransactionDetail.PriceBeforeDiscount - editPatientTransaction.Price;
							editedPatientTransactionPriceInquiryModel.CoveredQuantity = 0;
							editedPatientTransactionPriceInquiryModel.NotCoveredCompanyDiscount = editPatientTransaction.CompanyDiscountAmount;
							editedPatientTransactionPriceInquiryModel.CompanyDiscount = 0;
						}
						//  editPatientTransaction.PatientShare += editPatientTransaction.CompanyShare;
						editPatientTransaction.CompanyShare = decimal.Zero;
						editPatientTransaction.PatientTransactionDetail.IsNeedApproval = (int)BillingEnums.IsNeedApproval.Needapproval;
						editedPatientTransactionPriceInquiryModel.CompanyShare = decimal.Zero;
						editedPatientTransactionPriceInquiryModel.IsNeedApproval = (int)BillingEnums.IsNeedApproval.Needapproval;

						//========================set tax patient and company shares ===============================================================//
						if (editedPatientTransactionPriceInquiryModel.Taxes != null)
						{
							foreach (var tax in editPatientTransaction.PatientTransactionTaxes)
							{
								tax.TaxAmount += tax.CompanyTaxAmount;
								tax.TaxAmountDue += tax.CompanyTaxAmount;
								tax.CoveredPatientTaxAmount = tax.CoveredPatientTaxAmount;
								tax.CoveredPatientTaxAmountDue = tax.CoveredPatientTaxAmountDue;
								tax.CompanyTaxAmount = decimal.Zero;
							}
							foreach (var tax in editedPatientTransactionPriceInquiryModel.Taxes)
							{
								tax.PatientTaxAmount += tax.CompanyTaxAmount;
								tax.PatientTaxAmountDue += tax.CompanyTaxAmount;
								tax.CompanyTaxAmount = decimal.Zero;
							}
						}
						//==========================================================================================================================//
					}

					#endregion

					#region Add Edited Patient Transaction To Return Model 

					// add edit patient transaction to return model 
					result.Add(new PriceInquiryModel()
					{

						StatusCode = editedPatientTransactionPriceInquiryModel.StatusCode,
						IsValid = editedPatientTransactionPriceInquiryModel.IsValid,
						MessageResult = editedPatientTransactionPriceInquiryModel.IsValid ? string.Format(EngineMessages.RemainingPatientTransaction, parameter.VisitID, parameter.TransactionID) : editedPatientTransactionPriceInquiryModel.MessageResult,
						TransactionID = parameter.TransactionID,
						VisitServiceID = parameter.VisitServiceID,
						ProductID = editedPatientTransactionPriceInquiryModel.ProductID,
						PatientShare = editedPatientTransactionPriceInquiryModel.PatientShare,
						CompanyShare = editedPatientTransactionPriceInquiryModel.CompanyShare,
						Price = editedPatientTransactionPriceInquiryModel.Price,
						PackageShare = editedPatientTransactionPriceInquiryModel.PackageShare.HasValue ? Math.Round(editedPatientTransactionPriceInquiryModel.PackageShare.Value, 2) : null as decimal?,
						Inquantity = editedPatientTransactionPriceInquiryModel.Inquantity,
						PackageDiscount = editedPatientTransactionPriceInquiryModel.PackageDiscount,
						IsNeedApproval = parameter.FinancialStatus == (int)BillingEnums.FinancialStatus.Insured ? editedPatientTransactionPriceInquiryModel.IsNeedApproval : 0,
						ShowinAuthorization = editedPatientTransactionPriceInquiryModel.ShowinAuthorization,
						// partially 
						CoveredPriceBeforeDiscount = editedPatientTransactionPriceInquiryModel.CoveredPriceBeforeDiscount,
						CoveredPatientShare = editedPatientTransactionPriceInquiryModel.CoveredPatientShare,
						CoveredCompanyDiscount = editedPatientTransactionPriceInquiryModel.CoveredCompanyDiscount,
						NotCoveredCompanyDiscount = editedPatientTransactionPriceInquiryModel.NotCoveredCompanyDiscount,
						CompanyDiscountAmount = editedPatientTransactionPriceInquiryModel.CompanyDiscountAmount,
						InsuredPriceBeforeDiscount = editedPatientTransactionPriceInquiryModel.InsuredPriceBeforeDiscount,
						CoveredQuantity = editedPatientTransactionPriceInquiryModel.CoveredQuantity,
						ContractVisitLimit = editedPatientTransactionPriceInquiryModel.ContractVisitLimit,
						ContractTypeID = editedPatientTransactionPriceInquiryModel.ContractTypeID,
						ContractTypeName = editedPatientTransactionPriceInquiryModel.ContractTypeName,
						ContractTypeArName = editedPatientTransactionPriceInquiryModel.ContractTypeArName,
						PriceBeforeDiscount = editedPatientTransactionPriceInquiryModel.PriceBeforeDiscount,
						CompanyDiscount = editedPatientTransactionPriceInquiryModel.IsApplyAccommodationClassPricing == true && parameter.FinancialStatus == (int)BillingEnums.FinancialStatus.Insured ? editedPatientTransactionPriceInquiryModel.ActualCompanyDiscount : editedPatientTransactionPriceInquiryModel.CompanyDiscount,
						Taxes = editedPatientTransactionPriceInquiryModel.Taxes != null ? editedPatientTransactionPriceInquiryModel.Taxes.Select(a => new TaxModel()
						{
							PatientTaxAmount = a.PatientTaxAmount,
							PatientTaxAmountDue = a.PatientTaxAmountDue,
							TaxID = a.TaxID,
							CompanyTaxAmount = a.CompanyTaxAmount,
							PackageTaxAmount = a.PackageTaxAmount,
							TaxName = a.TaxName,
							CoveredPatientTaxAmount = a.CoveredPatientTaxAmount,
							CoveredPatientTaxAmountDue = a.CoveredPatientTaxAmountDue
						}).ToList() : new List<HMIS.Billing.Model.Custom.BillingEngine.TaxModel>()
					});

					#endregion

					#region Add After Edit Transaction To Return Model

					// add after to return model
					foreach (var transactionWithExtra in patientTransactionsAfterEdit)
					{
						var transaction = transactionWithExtra.PatientTransaction;
						decimal priceAfterRounding = Math.Round(transaction.Price.Value, 2);
						decimal CompnayShareAfterRounding = Math.Round(transaction.CompanyShare.Value, 2);
						PriceInquiryModel model = new PriceInquiryModel();
						if (transaction.Invalid == true)
						{
							model.StatusCode = transaction.StatusCode.Value;
							model.IsValid = false;
							model.MessageResult = transaction.MessageResult;
							model.TransactionID = transaction.ID;
							model.ProductID = transaction.ProductID;
							model.VisitServiceID = transaction.VisitServiceID;
							model.PatientShare = null;
							model.CompanyShare = null;
							model.Price = null;
							model.PackageShare = null;
							model.Inquantity = null;
							model.PackageDiscount = null;
							model.IsNeedApproval = 0;
							model.ShowinAuthorization = null;
							model.CompanyDiscount = null;
							model.PriceBeforeDiscount = null;
							model.CoveredPriceBeforeDiscount = null;
							model.CoveredPatientShare = null;
							model.CoveredCompanyDiscount = null;
							model.NotCoveredCompanyDiscount = null;
							model.CompanyDiscountAmount = null;
							model.InsuredPriceBeforeDiscount = null;
							model.CoveredQuantity = null;
							model.ContractVisitLimit = null;
							model.ContractTypeID = null;
							model.ContractTypeName = null;
							model.ContractTypeArName = null;
							model.Taxes = new List<HMIS.Billing.Model.Custom.BillingEngine.TaxModel>();
						}
						else
						{
							if ((anyTransactionHasExceedLimit || editTransactionHasExceedLimit) && latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && latestConfiguration.MarkServiceExceedLimit == (int)BillingEnums.ConfigMarkServiceExceedLimit.AllServices && transaction.PatientTransactionDetail?.IsClaim != true && transaction.PatientTransactionDetail?.ApprovalStatus == null)
							{
								model.PatientShare = (transaction.Inquantity > 0 ? Math.Round(transaction.PatientShare.Value, 2) : priceAfterRounding - CompnayShareAfterRounding) + CompnayShareAfterRounding;
								model.CompanyShare = decimal.Zero;
								model.IsNeedApproval = (int)BillingEnums.IsNeedApproval.Needapproval;
							}
							else
							{
								model.PatientShare = transaction.Inquantity > 0 ? Math.Round(transaction.PatientShare.Value, 2) : priceAfterRounding - CompnayShareAfterRounding;
								model.CompanyShare = CompnayShareAfterRounding;
								model.IsNeedApproval = transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured ? transaction.PatientTransactionDetail.IsNeedApproval.Value : 0;
							}
							model.StatusCode = BillingEnums.BillingEngineStatusCode.Valid;
							model.IsValid = true;
							model.MessageResult = string.Format(EngineMessages.RemainingPatientTransaction, transaction.VisitID, transaction.ID);
							model.TransactionID = transaction.ID;
							model.ProductID = transaction.ProductID;
							model.VisitServiceID = transaction.VisitServiceID;
							model.Price = priceAfterRounding;
							model.PackageShare = transaction.PackageShare.HasValue ? Math.Round(transaction.PackageShare.Value, 2) : null as decimal?;
							model.Inquantity = transaction.Inquantity;
							model.PackageDiscount = transaction.PackageDiscount;
							model.CompanyDiscount = transaction.CompanyDiscount;
							// partially 
							model.CoveredPriceBeforeDiscount = transaction.PatientTransactionDetail != null ? transaction.PatientTransactionDetail.CoveredPriceBeforeDiscount : transaction.PatientTransactionDetail.PriceBeforeDiscount;
							model.CoveredPatientShare = transaction.CoveredPatientShare;
							model.CoveredCompanyDiscount = transaction.CoveredCompanyDiscount;
							model.NotCoveredCompanyDiscount = transaction.NotCoveredCompanyDiscount;
							model.CompanyDiscountAmount = transaction.CompanyDiscountAmount;
							model.InsuredPriceBeforeDiscount = transaction.InsuredPriceBeforeDiscount;
							model.CoveredQuantity = transaction.CoveredQuantity;
							model.ContractVisitLimit = transaction.ContractVisitLimit;
							model.ContractTypeID = contractTypeInfo.ContractTypeID;
							model.ContractTypeName = contractTypeInfo.ContractTypeName;
							model.ContractTypeArName = contractTypeInfo.ContractTypeArName;
							model.ShowinAuthorization = transaction.PatientTransactionDetail.ShowInAuthorizationScreen;
							model.PriceBeforeDiscount = transaction.PatientTransactionDetail != null ? transaction.PatientTransactionDetail.PriceBeforeDiscount : transaction.Price;
							if (transaction.PatientTransactionDetail?.IsApplyAccommodationClassPricing == true && transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured && transaction.PatientTransactionDetail.ActualPriceBeforeDiscount.HasValue)
							{
								model.CompanyDiscount = transaction.ActualCompanyDiscount;
								model.PriceBeforeDiscount = transaction.FinancialStatusID == (int)BillingEnums.FinancialStatus.Insured ? transaction.PatientTransactionDetail.ActualPriceBeforeDiscount : transaction.Price;

							}
							if (model.PriceBeforeDiscount.HasValue)
							{
								model.PriceBeforeDiscount = Math.Round(model.PriceBeforeDiscount.Value, 2);
							}

							if ((anyTransactionHasExceedLimit || editTransactionHasExceedLimit) && latestConfiguration != null && latestConfiguration.IsApprovalExceedingCoLimit == true && latestConfiguration.MarkServiceExceedLimit == (int)BillingEnums.ConfigMarkServiceExceedLimit.AllServices && transaction.PatientTransactionDetail?.IsClaim != true && transaction.PatientTransactionDetail?.ApprovalStatus == null)
							{
								model.Taxes = transaction.PatientTransactionTaxes != null ? transaction.PatientTransactionTaxes.Where(a => a.IsDeleted == false).Select(a => new TaxModel()
								{
									PatientTaxAmount = Math.Round(a.TaxAmount.Value + a.CompanyTaxAmount.Value, 2),
									PatientTaxAmountDue = Math.Round(a.TaxAmountDue.Value + a.CompanyTaxAmount.Value, 2),
									TaxID = a.TaxID.Value,
									CompanyTaxAmount = decimal.Zero,
									PackageTaxAmount = a.PackageTaxAmount.HasValue ? Math.Round(a.PackageTaxAmount.Value, 2) : null as decimal?,
									TaxName = a.Tax != null ? a.Tax.EnName : _TaxManager.GetTaxEnNameByID(a.TaxID.Value),
									CoveredPatientTaxAmount = a.CoveredPatientTaxAmount.HasValue ? Math.Round(a.CoveredPatientTaxAmount.Value, 2) : null as decimal?,
									CoveredPatientTaxAmountDue = a.CoveredPatientTaxAmountDue.HasValue ? Math.Round(a.CoveredPatientTaxAmountDue.Value, 2) : null as decimal?,

								}).ToList() : new List<HMIS.Billing.Model.Custom.BillingEngine.TaxModel>();
							}
							else
							{
								model.Taxes = transaction.PatientTransactionTaxes != null ? transaction.PatientTransactionTaxes.Where(a => a.IsDeleted == false).Select(a => new TaxModel()
								{
									PatientTaxAmount = Math.Round(a.TaxAmount.Value, 2),
									PatientTaxAmountDue = Math.Round(a.TaxAmountDue.Value, 2),
									TaxID = a.TaxID.Value,
									CompanyTaxAmount = Math.Round(a.CompanyTaxAmount.Value, 2),
									PackageTaxAmount = a.PackageTaxAmount.HasValue ? Math.Round(a.PackageTaxAmount.Value, 2) : null as decimal?,
									TaxName = a.Tax != null ? a.Tax.EnName : _TaxManager.GetTaxEnNameByID(a.TaxID.Value),
									CoveredPatientTaxAmount = a.CoveredPatientTaxAmount.HasValue ? Math.Round(a.CoveredPatientTaxAmount.Value, 2) : null as decimal?,
									CoveredPatientTaxAmountDue = a.CoveredPatientTaxAmountDue.HasValue ? Math.Round(a.CoveredPatientTaxAmountDue.Value, 2) : null as decimal?,

								}).ToList() : new List<HMIS.Billing.Model.Custom.BillingEngine.TaxModel>();
							}

						}
						result.Add(model);
						_InsuredEngine.LogInsuredFinalResponse(model);
					}

					#endregion

					// save after update 
					_unitOfWork.BulkSaveChanges();
					ts.Complete();
				}
				catch (BillingEngineException ex)
				{
					throw ex;
				}
				catch (Exception ex)
				{

					ts.Dispose();
					throw new BillingEngineException("Error occured while saving" + ex.GetExceptionMessages(), (int)BillingEnums.BillingExceptionMessagesEnum.FrameworkExceptions);
				}
			}
			return result;
		}

		public List<TransactionTaxModel> AddDiscount(List<TransactionDiscountModel> parameters)
		{
			List<TransactionTaxModel> result = new List<TransactionTaxModel>();

			if (parameters.Count == 0) return result;
			TransactionOptions options = new TransactionOptions();
			options.IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted;
			options.Timeout = TimeSpan.FromMinutes(transactionTimeOut);
			using (TransactionScope ts = new TransactionScope(TransactionScopeOption.Required, options, TransactionScopeAsyncFlowOption.Enabled))
			{
				try
				{
					var episodeType = _PatientTransactionMngr.GetPatientTransactionByIDIncludingDetails(parameters[0].TransactionID).EpisodeTypeID;
					var latestConfiguration = _ConfigurationMngr.GetLastActiveConfiguration();
					var activeTaxes = _TaxManager.GetActiveTaxes(episodeType).ToList();

					foreach (TransactionDiscountModel parameter in parameters)
					{
						PatientTransaction patientTransaction = _PatientTransactionMngr.GetPatientTransactionByIDIncludingDetails(parameter.TransactionID);
						if (patientTransaction == null || patientTransaction.IsDeleted != false)
							throw new BillingEngineException("Transaction of ID " + parameter.TransactionID + " does not exist", (int)BillingEnums.BillingExceptionMessagesEnum.TransactionIDNotExist);
						else
						{
							// Set unique id to each request used in log (Database log in EngineLog table)
							LogAdditionalData logData = new LogAdditionalData { RequestID = Guid.NewGuid(), VisitID = patientTransaction.VisitID, EntityID = _UserInfo.OrgId };
							// Log in database that the request financial status is cash
							LogFactory<BillingEngineManagerFactory>.Log(LogLevel.Info, string.Format(EngineMessages.AddDiscount, parameter.TransactionID, parameter.DiscountPercentage), LogType.Database, null, logData);

							PriceInquiryBeforeRoundingModel priceInquiryBeforeRoundingModel = new PriceInquiryBeforeRoundingModel()
							{
								ProductID = patientTransaction.ProductID,
								Price = patientTransaction.Price,
								PriceBeforeDiscount = patientTransaction.PatientTransactionDetail != null ? patientTransaction.PatientTransactionDetail.PriceBeforeDiscount : patientTransaction.Price,
								CompanyShare = patientTransaction.CompanyShare,
								PatientShare = patientTransaction.PatientShare,
								SalesUntiConversionRate = patientTransaction.SalesUnitConversionRate,
								PackageShare = patientTransaction.PackageShare,
								CoveredPatientShare = patientTransaction.CoveredPatientShare,
								CoveredPrice = patientTransaction.CoveredPrice,
								CoveredPriceBeforeDiscount = patientTransaction.PatientTransactionDetail.CoveredPriceBeforeDiscount,
								CoveredQuantity = patientTransaction.CoveredQuantity,
								NotCoveredPrice = patientTransaction.NotCoveredPrice
							};

							PriceInquiryParameters priceInquiryParameters = new PriceInquiryParameters()
							{
								ProductID = patientTransaction.ProductID,
								NationalityID = patientTransaction.NationalityID,
								Discount = parameter.DiscountPercentage,
								ProductQuantity = patientTransaction.ProductQuantity,
								EpisodeType = patientTransaction.EpisodeTypeID
							};

							#region Calculate Tax With Discount On Patient Share

							// Calculate tax
							List<TaxModel> patientTransactionTaxes = _TaxCalculatorManagerFctry.TaxCalculationProcedures(latestConfiguration, activeTaxes, priceInquiryBeforeRoundingModel, priceInquiryParameters, logData);

							patientTransaction.PatientDiscount = parameter.DiscountPercentage;

							#endregion

							#region Delete Old Patient Transaction Tax

							// Delete old patient transaction tax
							_PatientTransactionMngr.DeleteOldPatientTransactionTax(patientTransaction.VisitServiceID.Value);

							#endregion

							#region Prepare Tax Model To Save

							foreach (TaxModel item in patientTransactionTaxes)
							{

								_patientTransactionTaxManager.AddNew(new PatientTransactionTax()
								{
									TaxAmount = item.PatientTaxAmount,
									TaxAmountDue = item.PatientTaxAmountDue,
									TaxID = item.TaxID,
									CompanyTaxAmount = item.CompanyTaxAmount,
									CoveredPatientTaxAmount = item.CoveredPatientTaxAmount,
									CoveredPatientTaxAmountDue = item.CoveredPatientTaxAmountDue
								});
								item.CompanyTaxAmount = Math.Round(item.CompanyTaxAmount, 2);
								item.PatientTaxAmountDue = Math.Round(item.PatientTaxAmountDue, 2);
								item.PatientTaxAmount = Math.Round(item.PatientTaxAmount, 2);
								item.CoveredPatientTaxAmount = Math.Round(item.CoveredPatientTaxAmount.Value, 2);
								item.CoveredPatientTaxAmountDue = Math.Round(item.CoveredPatientTaxAmountDue.Value, 2);
							}

							// Round tax values
							result.Add(new TransactionTaxModel() { TransactionID = parameter.TransactionID, Taxes = patientTransactionTaxes });

							#endregion
						}
					}
					_unitOfWork.BulkSaveChanges();
					ts.Complete();
				}
				catch (BillingEngineException ex)
				{
					throw ex;
				}
				catch (Exception ex)
				{
					ts.Dispose();
					throw new BillingEngineException("Error occured while saving" + ex.GetExceptionMessages(), (int)BillingEnums.BillingExceptionMessagesEnum.FrameworkExceptions);
				}
			}
			return result;
		}

		public List<TaxModel> CalculateMasterTax(long visitID)
		{
			List<TaxModel> result = new List<TaxModel>();

			TransactionOptions options = new TransactionOptions();
			options.IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted;
			options.Timeout = TimeSpan.FromMinutes(transactionTimeOut);
			using (TransactionScope ts = new TransactionScope(TransactionScopeOption.Required, options, TransactionScopeAsyncFlowOption.Enabled))
			{
				try
				{
					#region Calculate Master Tax

					// Calculate master tax
					List<TaxModel> patientTransactionTaxes = _TaxCalculatorManagerFctry.MasterTaxCalculationProcedures(visitID);

					#endregion

					#region Delete old patient transaction master tax by visit id

					// Delete old patient transaction master tax by visit id
					_PatientTransactionTaxMasterMngr.DeleteOldPatientTransactionMasterTax(visitID);

					#endregion

					#region Assign new tax to patient transaction master tax and round values 

					// Assign new tax to patient transaction master tax and round values 
					patientTransactionTaxes.ForEach(delegate (HMIS.Billing.Model.Custom.BillingEngine.TaxModel tax)
					{
						_PatientTransactionTaxMasterMngr.Add(new PatientTransactionTaxMaster()
						{
							VisitID = visitID,
							TaxAmount = tax.PatientTaxAmount,
							TaxAmountDue = tax.PatientTaxAmountDue,
							TaxID = tax.TaxID,
							CompanyTaxAmount = tax.CompanyTaxAmount,
							PackageTaxAmount = tax.PackageTaxAmount,
							CoveredPatientTaxAmount = tax.CoveredPatientTaxAmount,
							CoveredPatientTaxAmountDue = tax.CoveredPatientTaxAmountDue
						});
						result.Add(new TaxModel()
						{
							CompanyTaxAmount = Math.Round(tax.CompanyTaxAmount, 2),
							PatientTaxAmountDue = Math.Round(tax.PatientTaxAmountDue, 2),
							PatientTaxAmount = Math.Round(tax.PatientTaxAmount, 2),
							PackageTaxAmount = Math.Round(tax.PackageTaxAmount ?? 0, 2),
							TaxID = tax.TaxID,
							TaxName = tax.TaxName,
							CoveredPatientTaxAmount = Math.Round(tax.CoveredPatientTaxAmount ?? 0, 2),
							CoveredPatientTaxAmountDue = Math.Round(tax.CoveredPatientTaxAmountDue ?? 0, 2),

						});
					});

					_unitOfWork.BulkSaveChanges();

					ts.Complete();

					#endregion
				}
				catch (BillingEngineException ex)
				{
					throw ex;
				}
				catch (Exception ex)
				{
					ts.Dispose();
					throw new BillingEngineException("Error occured while saving" + ex.GetExceptionMessages(), (int)BillingEnums.BillingExceptionMessagesEnum.FrameworkExceptions);
				}
			}
			return result;
		}

		public int ServiceApprovalStatus(List<VisitServicesList> VisitServicesList)
		{
			int IsSuccessful = (int)BillingEnums.ContractResult.Failed;
			try
			{
				List<VisitServicesList> getNewOrRejectList = VisitServicesList.Where(a => a.ApprovalStatus == (int)BillingEnums.ApprovalStatus.Rejected || a.ApprovalStatus == null).ToList();
				List<long> visitserviceIds = getNewOrRejectList.Select(a => a.VisitServiceTransactionID).Distinct().ToList();
				var patientTransactions = _PatientTransactionMngr.GetPatientTransactionsByVisitServiceID(visitserviceIds);
				foreach (var item in getNewOrRejectList)
				{
					PatientTransaction patientTransaction = patientTransactions.Where(a => a.VisitServiceID == item.VisitServiceTransactionID).FirstOrDefault();
					if (patientTransaction != null)
					{
						var patientTransactionDetail = patientTransaction.PatientTransactionDetail;
						patientTransactionDetail.ApprovalStatus = item.ApprovalStatus;
					}
				}
				_unitOfWork.SaveChanges();
				IsSuccessful = (int)BillingEnums.ContractResult.Success;
			}
			catch (Exception ex)
			{
				IsSuccessful = (int)BillingEnums.ContractResult.Failed;
			}
			return IsSuccessful;
		}
		public int ServiceClaimStatus(List<VisitServicesList> VisitServicesList)
		{
			int IsSuccessful = (int)BillingEnums.ContractResult.Failed;
			try
			{
				List<VisitServicesList> getClaimedOrNoClaimedList = VisitServicesList.Where(a => a.ServiceClaimStatus == (int)BillingEnums.ServiceClaimStatus.NotClaimed || a.ServiceClaimStatus == (int)BillingEnums.ServiceClaimStatus.Claimed).ToList();
				List<long> visitserviceIds = getClaimedOrNoClaimedList.Select(a => a.VisitServiceTransactionID).Distinct().ToList();
				var patientTransactions = _PatientTransactionMngr.GetPatientTransactionsByVisitServiceID(visitserviceIds);
				foreach (var item in getClaimedOrNoClaimedList)
				{
					PatientTransaction patientTransaction = patientTransactions.Where(a => a.VisitServiceID == item.VisitServiceTransactionID).FirstOrDefault();
					if (patientTransaction != null)
					{
						var patientTransactionDetail = patientTransaction.PatientTransactionDetail;
						patientTransactionDetail.IsClaim = item.ServiceClaimStatus == (int)BillingEnums.ServiceClaimStatus.NotClaimed ? false : true;
					}
				}
				_unitOfWork.SaveChanges();
				IsSuccessful = (int)BillingEnums.ContractResult.Success;
			}
			catch (Exception ex)
			{
				IsSuccessful = (int)BillingEnums.ContractResult.Failed;
			}
			return IsSuccessful;
		}


		public void MapPatientTransactionModelKSA( PatientTransaction item)
        {
			bool treatPatient = false;
			VisitServiceWithCashPrice visitServiceWithCashPrice = _InsuredEngine.GetCashPricePerVisitServiceID(item.VisitServiceID.Value, out treatPatient);
			if (treatPatient == true)
			{
				item.PatientShare = visitServiceWithCashPrice.CashPrice;
				item.Price = item.PatientShare;
				item.PatientTransactionDetail.PriceBeforeDiscount = item.PatientTransactionDetail.ActualPriceBeforeDiscount = visitServiceWithCashPrice.CashPrice;
				item.PatientTransactionDetail.UnitPriceBeforeDiscount = item.PatientTransactionDetail.PriceBeforeDiscount / item.ProductQuantity;
				item.CoveredCompanyDiscount = 0;
				item.CoveredCompanyShare = 0;
				item.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
				item.CoveredPatientShare = 0;
				item.CompanyDiscountAmount = item.PatientTransactionDetail.PriceBeforeDiscount - item.Price;
				item.CoveredQuantity = 0;
				item.NotCoveredQuantity = item.ProductQuantity.Value - item.Inquantity;
				item.NotCoveredCompanyDiscount = item.CompanyDiscountAmount;
				item.PriceListID = visitServiceWithCashPrice.CashPriceList.ID;
				item.PricePlanID = visitServiceWithCashPrice.CashPricePlan.ID;
				item.PricePlanRangeSetID = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.ID : null as long?;
				item.PricePlanRangeSetRank = visitServiceWithCashPrice.CashPricePlanRangeSet != null ? visitServiceWithCashPrice.CashPricePlanRangeSet.Rank : null as int?;
				item.CompanyDiscount = 0;
				item.CoveredPrice = 0;
			}
			else if (treatPatient == false)
			{
				item.PatientShare = item.PatientTransactionDetail.PriceBeforeDiscount;
				item.Price = item.PatientShare;
				item.CoveredCompanyDiscount = 0;
				item.CoveredCompanyShare = 0;
				item.PatientTransactionDetail.CoveredPriceBeforeDiscount = 0;
				item.CoveredPatientShare = 0;
				item.CompanyDiscountAmount = item.PatientTransactionDetail.PriceBeforeDiscount - item.Price;
				item.CoveredQuantity = 0;
				item.NotCoveredQuantity = item.ProductQuantity.Value - item.Inquantity;
				item.NotCoveredCompanyDiscount = item.CompanyDiscountAmount;
				item.CompanyDiscount = 0;
				item.CoveredPrice = 0;
			}
			//  item.PatientShare += item.CompanyShare;
			item.CompanyShare = decimal.Zero;
			item.PatientTransactionDetail.IsNeedApproval = (int)BillingEnums.IsNeedApproval.Needapproval;

			////========================set tax patient and company shares ===============================================================//
			//foreach (var tax in item.PatientTransactionTaxes)
			//{
			//	tax.TaxAmount += tax.CompanyTaxAmount;
			//	tax.TaxAmountDue += tax.CompanyTaxAmount;
			//	tax.CoveredPatientTaxAmount = tax.CoveredPatientTaxAmount;
			//	tax.CoveredPatientTaxAmountDue = tax.CoveredPatientTaxAmountDue;
			//	tax.CompanyTaxAmount = decimal.Zero;
			//}
		}
	
	}
}
