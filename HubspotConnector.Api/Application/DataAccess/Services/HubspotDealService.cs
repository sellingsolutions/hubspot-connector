using System.Linq;
using System.Threading.Tasks;
using HubspotConnector.Application.DataAccess.Repositories;
using HubspotConnector.Application.Dto;
using HubspotConnector.CrossCuttingConcerns;
using iSpectAPI.Core.Application.DataAccess.Clients.CouchbaseClients;
using iSpectAPI.Core.Application.Extensions;
using iSpectAPI.Core.Application.Extensions.Reflection;
using iSpectAPI.Core.Database.ActorModel.Actors;
using iSpectAPI.Core.Database.HubspotConnector.Associations;
using iSpectAPI.Core.Database.HubspotConnector.Deals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skarp.HubSpotClient.Deal;
using Skarp.HubSpotClient.Deal.Dto;

namespace HubspotConnector.Application.DataAccess.Services
{
    public class HubspotDealService : IHubspotDealService
    {
        private readonly ICBSClient _db;
        private readonly HsAppSettings _appSettings;
        private readonly ILogger<IHubspotDealService> _logger;
        private readonly IHubspotOwnerRepository _hubspotOwnerRepository;
        private readonly IHubspotContactRepository _hubspotContactRepository;

        private HubSpotDealClient _hubSpotDealClient { get; set; }

        public HubspotDealService(
            IOptions<HsAppSettings> appSettings,
            ICBSClient db,
            ILogger<IHubspotDealService> logger,
            IHubspotOwnerRepository hubspotOwnerRepository,
            IHubspotContactRepository hubspotContactRepository)
        {
            _db = db;
            _logger = logger;
            _appSettings = appSettings.Value;
            _hubspotOwnerRepository = hubspotOwnerRepository;
            _hubspotContactRepository = hubspotContactRepository;
            
            _hubSpotDealClient = new HubSpotDealClient(_appSettings.ApiKey);
        }
        
        public async Task<HsDeal> CreateDeal(HubspotDealRequest request)
        {
            var name = $"#{request.Project.ReferenceId} - {request.Project.Name} - {request.Name}";
            var existingDeal = await GetDeal(request.Project.Id, name);
            if (existingDeal != null)
            {
                _logger.LogInformation($"{GetType().Name} ABORTING! DEAL ALREADY EXISTS {name} {existingDeal.Id} {existingDeal.HsId}");
                return null;
            }
            
            var ownerEmail = await request.DealOwner.GetLatestEmail(nameof(IsActor.EmailAddressIds), _db);
            var hsOwner = await _hubspotOwnerRepository.GetOwnerByEmail(ownerEmail);

            var customerContact = await _hubspotContactRepository.GetContactByEmail(request.CustomerEmail);
            if (customerContact == null && request.CustomerEmail.IsNotNullOrEmpty())
            {
                customerContact = await _hubspotContactRepository.CreateContact(request.Customer);
            }

            var contactCompany = await _hubspotContactRepository.GetContactCompany(customerContact?.Id ?? 0);
            if (contactCompany == null && request.Customer is IsCompany company)
                contactCompany = await _hubspotContactRepository.CreateCompany(company);
            if (contactCompany == null && request.CustomerParty.Company.IsNotNullOrEmpty())
                contactCompany = await _hubspotContactRepository.CreateCompany(request.CustomerParty.Company);

            var dealDto = new HubspotDealDto
            {
                Url = request.Url,
                OwnerId = hsOwner.Id,
                DealType = request.DealType,
                Name = $"[ISPECT] {name}",
                Amount = request.DealValue,
                Pipeline = _appSettings.DefaultPipeline,
                Stage = _appSettings.DefaultDealStage,
                CloseDate = $"{request.CloseDate.ToEpochMillis()}",
                NoOfApartments = request.NoOfApartments,
                NoOfApartmentBuildings = request.NoOfApartmentBuildings
            };

            if (contactCompany != null)
                dealDto.Associations.AssociatedCompany = new[] { contactCompany.Id ?? 0 };
            if (customerContact != null)
                dealDto.Associations.AssociatedContacts = new[] { customerContact.Id ?? 0 };

            var response = await _hubSpotDealClient.CreateAsync<DealHubSpotEntity>(dealDto);
            var hsId = response.Id ?? 0;

            _logger.LogInformation($"{GetType().Name} CREATED HUBSPOT DEAL {hsId} owner:{ownerEmail} customer:{request.CustomerEmail} {name} deal value: {dealDto.Amount} {request.Url}");

            var deal = await HsDeal.Ensure<HsDeal>(hsId, _db);
            deal.Name = name;
            deal.ProjectId = request.Project.Id;
            deal.OwnerId = hsOwner.Id;
            deal.PartyId = request.CustomerParty.Id;

            var dealAssociations = await HsAssociation.Ensure<HsDealAssociations>(hsId, _db);
            if (contactCompany?.Id != null)
                dealAssociations.CompanyIds = new[] { contactCompany.Id ?? 0 };

            if (customerContact?.Id != null)
                dealAssociations.ContactIds = new[] { customerContact.Id ?? 0 };

            dealDto.CopyPropsTo(deal);

            return await _db.Update(deal);
        }

        public async Task<HsDeal> GetDeal(string projectId, string name)
        {
            var statement = "SELECT meta().id " +
                            "FROM ispect " +
                            $"WHERE type = '{typeof(HsDeal).GetDocumentType()}' " +
                            $"AND `projectID` = '{projectId}' " +
                            $"AND `name` = '{name}' ";
            
            var result = await _db.ExecuteStatement<HsDeal>(statement);
            return result.FirstOrDefault();
        }
    }
}