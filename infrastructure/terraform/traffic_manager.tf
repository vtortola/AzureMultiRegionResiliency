resource "azurerm_traffic_manager_profile" "dns_load_balancer" {
  name                   = "${local.resource_prefix}-traffic-manager"
  resource_group_name    = azurerm_resource_group.region_rgs[var.main_region].name
  traffic_routing_method = "Geographic"
  tags                   = local.tags

  dns_config {
    relative_name = local.resource_prefix # *.trafficmanager.net
    ttl           = 30
  }

  monitor_config {
    protocol = "HTTP"
    port     = 7000
    path     = "/ready"
  }
}

resource "azurerm_traffic_manager_profile" "regional_failover" {
  for_each               = local.all_regions
  name                   = "${local.resource_prefix}-failover-${each.key}"
  resource_group_name    = azurerm_resource_group.region_rgs[var.main_region].name
  traffic_routing_method = "Priority"
  tags                   = local.tags

  dns_config {
    relative_name = "${local.resource_prefix}-${each.key}"
    ttl           = 30
  }

  monitor_config {
    protocol                     = "HTTP"
    port                         = 7000
    path                         = "/ready"
    interval_in_seconds          = 30
    timeout_in_seconds           = 5
    tolerated_number_of_failures = 3
  }
}

resource "azurerm_traffic_manager_external_endpoint" "main_region" {
  for_each          = local.all_regions
  name              = "${local.resource_prefix}-${var.main_region}"
  profile_id        = azurerm_traffic_manager_profile.regional_failover[each.key].id
  target            = trimsuffix(azurerm_dns_cname_record.app_cname.fqdn, ".")
  endpoint_location = var.main_region

  priority = each.key == var.main_region ? 1 : 2
}

locals {
  secondary_endpoint_pairs = setproduct(local.all_regions, var.secondary_regions)

  secondary_endpoints = {
    for pair in local.secondary_endpoint_pairs : "${pair[0]}/${pair[1]}" => {
      profile_region = pair[0]
      api_region     = pair[1]
    }
  }

  secondary_regions_alphabetical = sort(tolist(var.secondary_regions))
}

resource "azurerm_traffic_manager_external_endpoint" "secondary_region" {
  for_each          = local.secondary_endpoints
  name              = "${local.resource_prefix}-${each.value.api_region}"
  profile_id        = azurerm_traffic_manager_profile.regional_failover[each.value.profile_region].id
  target            = trimsuffix(azurerm_dns_cname_record.aci_cname[each.value.api_region].fqdn, ".")
  endpoint_location = each.value.api_region

  priority = each.value.api_region == each.value.profile_region ? 1 : index(local.secondary_regions_alphabetical, each.value.api_region) + 3
}

resource "azurerm_traffic_manager_nested_endpoint" "regional_affinity" {
  for_each                = local.all_regions
  name                    = "${local.resource_prefix}-affinity-${each.key}"
  profile_id              = azurerm_traffic_manager_profile.dns_load_balancer.id
  target_resource_id      = azurerm_traffic_manager_profile.regional_failover[each.key].id
  endpoint_location       = each.key
  geo_mappings            = var.geo_mappings[each.key]
  minimum_child_endpoints = 1

  depends_on = [
    azurerm_traffic_manager_external_endpoint.main_region,
    azurerm_traffic_manager_external_endpoint.secondary_region
  ]
}
