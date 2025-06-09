# Deploying the service

1. **Prepare the deployment machine and Azure account**

   The deployment needs an existing local Git checkout, an active Azure subscription and an owned domain with access to its DNS configuration. The example uses East US 2 and West US 2. The subscription must allow the required services in both regions. Student subscriptions can have restrictions, and several resources, including the Premium container registry, are outside the free tier.

   The commands run in Bash on Linux, macOS or Windows through WSL 2. The following tools must already be installed:

   | Tool | Reference version | Purpose |
   | --- | --- | --- |
   | [Terraform](https://releases.hashicorp.com/terraform/1.11.4/) | 1.11.4 | Creates and manages the infrastructure. |
   | [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) | 2.73.0 | Authenticates the deployment and the image publication script. |
   | [Docker Desktop](https://docs.docker.com/desktop/release-notes/#4412) | 4.41.2 | Provides a running Docker Engine and Buildx. Linux can use Engine 28.1.1 with Buildx 0.23.0 instead. |
   | Git | 2.39.x | Provides the commit identifier used as the image tag. |
   | Bash | 3.2 or 5.2 | Runs the deployment commands. |
   | curl and dig | curl 7.81.x / dig 9.18.x; macOS curl 8.7.x / dig 9.10.x also support these commands | Check HTTP responses and DNS resolution. |
   | GNU Make | 3.81 or 4.3 | Runs the existing image update commands, when an update is needed. |

   A local .NET SDK is unnecessary. The [Dockerfile](../../src/SocialApp.WebApi/Dockerfile) builds and runs the application with .NET 8 inside containers.

   Docker must run Linux containers. The publishing script builds a `linux/amd64` image and then runs `docker push`. It therefore needs a builder that loads the image into the local Docker image store. The standard Docker builder provides this behaviour and can be selected with `docker buildx use default`. Docker Desktop also provides emulation on ARM machines.

   **Checkpoint:** the following command succeeds, shows `Driver: docker`, and includes `linux/amd64` in the supported platforms.

   ```bash
   docker buildx inspect --bootstrap
   ```

2. **Sign in and select the subscription**

   The following commands authenticate the Azure CLI and select the subscription used by Terraform. The placeholder is replaced with the subscription ID shown in the Azure portal.

   ```bash
   az login
   export ARM_SUBSCRIPTION_ID="<subscription-id>"
   az account set --subscription "$ARM_SUBSCRIPTION_ID"
   ```

   `ARM_SUBSCRIPTION_ID` is the only environment variable that needs exporting for this procedure. It must be set again in a new shell session. Terraform uses the Azure CLI login; no application client secret is required. Existing `ARM_*` authentication overrides or `TF_VAR_*` values from another project must not select different credentials or inputs.

   **Checkpoint:** the following command shows the intended subscription, tenant and signed-in account.

   ```bash
   az account show --output table
   ```

3. **Check deployment permissions and register Azure services**

   This procedure assumes a dedicated development subscription. In the Azure portal, its **Access control (IAM)** page must grant the deployment user **Owner** and **Key Vault Secrets Officer**. Owner allows resource creation and role assignments, but does not grant access to Key Vault secret contents. The second role allows Terraform to create and maintain the configuration secrets in the vaults it will create.

   These subscription-level roles are broad. In a shared subscription, its administrator should arrange equivalent access scoped to the deployment resources. The Terraform code grants permissions to the application identities, but does not grant secret-management permissions to the deployment user.

   In **Subscriptions > Resource providers**, the following namespaces must be registered: `Microsoft.App`, `Microsoft.ContainerInstance`, `Microsoft.ContainerRegistry`, `Microsoft.DocumentDB`, `Microsoft.KeyVault`, `Microsoft.ManagedIdentity`, `Microsoft.Network`, `Microsoft.OperationalInsights`, `Microsoft.Insights` and `Microsoft.Storage`.

   Registration is necessary because [main.tf](./main.tf) disables automatic provider registration. The networking namespace is `Microsoft.Network`; the `Microsoft.Networking` example in a source comment is incorrect.

   **Checkpoint:** the following command lists registered namespaces. All the namespaces above must appear before continuing. Role assignments and registrations can take several minutes to become effective.

   ```bash
   az provider list --query "[?registrationState=='Registered'].namespace" --output tsv
   ```

4. **Create storage for Terraform state**

   Terraform needs its backend before it can create application resources. In the Azure portal, a separate resource group and Storage Account are created for state, using **StorageV2**, **Standard LRS** and disabled anonymous blob access. The storage account name must be globally unique, with 3–24 lowercase letters or digits. A private blob container named `tfstate` is created inside it.

   On that storage account's **Access control (IAM)** page, the deployment user receives **Storage Blob Data Contributor**. This grants access to the state blob through the Azure CLI identity. The [Azure backend](https://developer.hashicorp.com/terraform/language/backend/azurerm) uses a blob lease to prevent concurrent state writes.

   From the repository root, the working directory changes to the Terraform directory:

   ```bash
   cd infrastructure/terraform
   ```

   A local `backend.hcl` file holds the actual names. The placeholders below are replaced with the resources just created. The key identifies this environment's state; another environment needs another key.

   ```hcl
   resource_group_name  = "<state-resource-group>"
   storage_account_name = "<state-storage-account>"
   container_name       = "tfstate"
   key                  = "socialapp-test.tfstate"
   use_azuread_auth      = true
   use_cli              = true
   ```

   This file overrides the original project's backend names without changing `main.tf`. State and saved plans can contain database keys and must not be published. Local backend configuration and generated Terraform files should also stay outside source control; the repository does not ignore all of them automatically.

   **Checkpoint:** with the actual storage account name, this command returns `tfstate`. An authorization error means that the role assignment, its propagation or storage network access still needs attention.

   ```bash
   az storage container show --account-name "<state-storage-account>" --name tfstate --auth-mode login --query name --output tsv
   ```

5. **Create and delegate the parent DNS zone**

   The application needs a parent DNS zone in the same Azure subscription already created. The Terraform stack uses `socialapp-dns-rg` to indicate the resource group of the already existing DNS zone.

   This prototype uses the domain `socialapp.x81.io` as DNS zone because it is owned by the author. Remember to use your own DNS zone.

   At the authoritative DNS provider for the owned domain, an NS record for the `socialapp` subdomain delegates it to all those name servers.

   Terraform will create and delegate the environment and regional zones beneath this parent. It does not create the parent zone or configure the external DNS provider.

   **Checkpoint:** the following command, using the real zone name, returns the Azure name servers. DNS propagation may require a delay before they appear.

   ```bash
   dig +short NS socialapp.x81.io
   ```

6. **Set the environment inputs**

   A local `terraform.tfvars` file in this directory holds the deployment configuration. Terraform loads it automatically, so no additional environment-variable setup or repeated command-line parameters are needed.

   ```hcl
   solution_name    = "sa82f1"
   environment_name = "test"
   dns_zone_rg     = "socialapp-dns-rg"
   dns_zone_name   = "socialapp.x81.io"

   geo_mappings = {
     eastus2 = ["GEO-AF", "GEO-EU", "GEO-ME", "GEO-AS"]
     westus2 = ["GEO-NA", "GEO-SA", "GEO-AN", "GEO-AP"]
   }
   ```

   `solution_name` must be replaced with a short, unique lowercase name. Terraform uses it in globally named resources such as the registry, Cosmos DB accounts, vaults and Traffic Manager profiles. Vault names follow `<solution>-<environment>-<region>` and must fit within 24 characters. The DNS values must match step 5.

   The remaining inputs keep their defaults: `eastus2` as the primary region, `westus2` as the secondary region, a Premium registry and Standard vaults. The mapping above adds Asia, which is missing from the default geographic mapping. A different region selection also needs matching mapping keys.

   **`image_tag` stays unset for the first deployment.** Its default is `null`, which enables the existing build-and-publish script. A container registry and an initial application image do not need to exist beforehand.

   **Checkpoint:** the following command shows the short commit identifier that the publishing script will use as the first image tag. A source archive without Git history is insufficient for that script.

   ```bash
   git rev-parse --short HEAD
   ```

7. **Initialise Terraform**

   All remaining Terraform commands run from this directory. For a fresh deployment, initialisation uses the backend configuration prepared in step 4:

   ```bash
   terraform init
   ```

   The lock file selects AzureRM `4.27.0`, Random `3.7.2` and Shell `1.7.10`. The command downloads these providers and connects to state storage. There is no need to upgrade them for this deployment. If the directory was previously connected to another backend, its state must be identified before accepting a backend change or migration.

   **Checkpoint:** validation reports that the configuration is valid.

   ```bash
   terraform validate
   ```

8. **Review the deployment plan**

   ```bash
   terraform plan
   ```

   **Checkpoint:** the plan creates resources with the chosen solution and environment names in the expected regions. On a fresh deployment, no existing application resources should be deleted. The plan must include `shell_script.publish[0]`, which confirms that automatic image publication is enabled. The image tag may remain unknown until apply because the script supplies it.

   Is important to resolve any naming, subscription-policy or configuration error before continuing.

9. **Deploy the service**

   ```bash
   terraform apply
   ```

   Terraform shows the plan again and requests confirmation. It creates Azure Container Registry, then runs [scripts/deploy.sh](./scripts/deploy.sh) through [deploy.tf](./deploy.tf). The script signs Docker into that registry, builds the application for `linux/amd64`, pushes it to the `socialapp` repository and returns the short Git commit identifier as its tag. Terraform passes that tag to the regional application resources.

   The same apply creates the databases, vault configuration, managed identities, hosting, logs, DNS records and Traffic Manager. The application receives `REGION`, `ENVIRONMENT`, `KEY_VAULT_URI`, `MANAGED_CLIENT_ID`, `IMAGE_TAG`, `MAIN_REGION` and `SEC_REGIONS` from Terraform. The publishing script receives `ACR_NAME` and `ACR_URI` from Terraform. None of these variables needs exporting on the deployment machine.

   **Checkpoint:** after apply completes successfully, the following command displays the registry name and regional health URLs.

   ```bash
   terraform output
   ```

10. **Check the regional services**

    The URLs in `main_url` and `secondary_url` use HTTP on port `7000`. The following command uses the primary URL from the output; the same check is repeated with the secondary URL.

    ```bash
    curl --fail --silent --show-error http://api.eastus2.test.socialapp.x81.io:7000/health
    ```

    **Checkpoint:** the response contains `status: OK`, the expected `region`, the Git tag in `version`, and `config: KeyVault`. The real domain replaces the example domain in this and the following commands.

    For checking readiness, the same URL uses `/ready`:

    ```bash
    curl --fail --include http://api.eastus2.test.socialapp.x81.io:7000/ready
    ```

    **Checkpoint:** both regions return HTTP `200`. Readiness checks User and Session container access. It does not verify Account availability or completion of background updates.

    This deployment exposes public HTTP endpoints. Its TCP ingress and DNS aliases do not provide HTTPS, so real credentials and personal data require a separate TLS setup.

11. **Check the global address**

    The global address is `api.<environment>.<parent-zone>`. It is not included in the Terraform outputs.

    ```bash
    dig +short api.test.socialapp.x81.io
    ```

    **DNS checkpoint:** the answer follows the Traffic Manager name and resolves to a regional endpoint. If it does not resolve, the parent delegation and the environment's NS records need checking.

    ```bash
    curl --fail --silent --show-error http://api.test.socialapp.x81.io:7000/health
    ```

    **HTTP checkpoint:** the response reports a healthy regional service. Traffic Manager selects a geographic preference and then an available endpoint through its nested Priority profiles. Health detection and DNS caches can delay routing changes.

    A final `terraform plan` should report no pending changes. Passing these checks confirms deployment health; it does not replace application tests or a failover exercise.

12. **Resolve deployment failures when necessary**

    Image replication and role propagation can take longer than resource creation. After the cause is checked, another `terraform apply` can complete a partially successful deployment using the same state and inputs.

    | Error | Action |
    | --- | --- |
    | Key Vault access denied during apply | Check the deployment user's Key Vault Secrets Officer role from step 3. Application identities have separate permissions managed by Terraform. |
    | Image missing locally before push | Check that Buildx uses the `docker` driver. The script does not use `--load`, so a builder that keeps the image only in its build cache will not work. |
    | Regional image pull fails | Check image publication, the managed identity's `AcrPull` assignment and registry replication. A delay may be needed before retrying. |
    | Provider, policy or quota error | Check registration and subscription restrictions. Repeating apply cannot fix missing permissions or unavailable capacity. |
    | API does not start | The regional container's logs in the Azure portal show configuration, permission and database errors. |
    | Resource already exists after a timeout | Compare the Azure resource with Terraform state. An existing resource belonging to this deployment may need `terraform import` with its exact resource address and ID. It should not be deleted blindly. |

    **Checkpoint for a resource left after a timeout:** the following command lists resources already tracked by Terraform. After recovery, the regional and global health checks must pass again.

    ```bash
    terraform state list
    ```

13. **Publish and deploy an application update when needed**

    The initial publication resource does not automatically rebuild on every source change. The existing [Makefile](../Makefile) handles later image publication. These commands run from `infrastructure`, one directory above the Terraform configuration:

    ```bash
    cd ..
    make docker_login
    make docker_build_and_push
    ```

    **Checkpoint:** this command returns the published image's digest. The registry name comes from step 9, and the tag appears in the push output. An update should use a new source commit and tag; reusing a tag can leave Terraform with no image change to deploy.

    ```bash
    az acr repository show --name "<registry-name>" --image "socialapp:<published-tag>" --query digest --output tsv
    ```

    In `terraform/terraform.tfvars`, the published tag is added or updated:

    ```hcl
    image_tag = "<published-commit-tag>"
    ```

    After `cd terraform`, `terraform plan` shows the update and `terraform apply` deploys it. The tag must remain in the variable file for later runs. Removing it re-enables automatic image publication. Old and new versions can run at the same time across regions, so both must understand the shared data formats.

    **Checkpoint:** the regional `/health` commands from step 10 report the new version, and `/ready` returns HTTP `200` in both regions.

14. **Remove the environment after use**

    With the original backend and `terraform.tfvars` still in place:

    ```bash
    terraform destroy
    ```

    The command displays the deletion plan and requests confirmation. It deletes the managed application resources, including databases and their data. Any required data must be backed up before confirmation.

    **Checkpoint:** after successful removal, the following command lists no managed resources.

    ```bash
    terraform state list
    ```

    The separately created parent DNS zone, its external NS delegation and state storage remain. They need separate removal if they are dedicated to this experiment. State storage must stay available until destruction succeeds. Soft-deletion or purge restrictions can delay vault cleanup or reuse of its name.
